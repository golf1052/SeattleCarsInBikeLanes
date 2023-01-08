using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure.Maps.Search;
using Azure.Maps.Search.Models;
using Azure.Storage.Blobs;
using golf1052.Mastodon;
using golf1052.Mastodon.Models.Accounts;
using ImageMagick;
using LinqToTwitter;
using LinqToTwitter.OAuth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UploadController : ControllerBase
    {
        public const string InitialUploadPrefix = "initialupload/";
        public const string FinalizedUploadPrefix = "finalizedupload/";

        private readonly BoundingBox SeattleBoundingBox = new BoundingBox(
            new Position(-122.436522, 47.495082),
            new Position(-122.235787, 47.735525));
        private readonly ILogger<UploadController> logger;
        private readonly ComputerVisionClient computerVisionClient;
        private readonly MapsSearchClient mapsSearchClient;
        private readonly BlobContainerClient blobContainerClient;
        private readonly MastodonClientProvider mastodonClientProvider;
        private readonly SlackbotProvider slackbotProvider;
        private readonly HelperMethods helperMethods;

        public UploadController(ILogger<UploadController> logger,
            ComputerVisionClient computerVisionClient,
            MapsSearchClient mapsSearchClient,
            BlobContainerClient blobContainerClient,
            MastodonClientProvider mastodonClientProvider,
            SlackbotProvider slackbotProvider,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.computerVisionClient = computerVisionClient;
            this.mapsSearchClient = mapsSearchClient;
            this.blobContainerClient = blobContainerClient;
            this.mastodonClientProvider = mastodonClientProvider;
            this.slackbotProvider = slackbotProvider;
            this.helperMethods = helperMethods;
        }

        [HttpPost("Initial")]
        public async Task<IActionResult> UploadPhoto()
        {
            if (Request.ContentLength == 0)
            {
                return BadRequest("Error: No photo uploaded.");
            }

            string tempFile = Path.GetTempFileName();
            try
            {
                using var fileStream1 = System.IO.File.OpenWrite(tempFile);
                await Request.BodyReader.CopyToAsync(fileStream1);
                fileStream1.Dispose();
                DateTime? photoDate = GetPhotoDate(tempFile);
                if (photoDate == null)
                {
                    return BadRequest("Error: Photo does not contain a date taken.");
                }

                Position? photoLocation = GetPhotoLocation(tempFile);
                if (photoLocation == null)
                {
                    return BadRequest("Error: Photo does not contain a location.");
                }

                if (!SeattleBoundingBox.Contains(photoLocation))
                {
                    return BadRequest("Error: Photo not taken in Seattle.");
                }

                // Ensure image is jpeg and is small enough to upload to Computer Vision
                string tempFile2 = Path.GetTempFileName();
                using var jpeg = new MagickImage(tempFile);
                jpeg.Format = MagickFormat.Jpeg;
                if (jpeg.Width > jpeg.Height && jpeg.Width > 1920)
                {
                    jpeg.Resize(1920, 1080);
                }
                else if (jpeg.Height > jpeg.Width && jpeg.Height > 1920)
                {
                    jpeg.Resize(1080, 1920);
                }
                await jpeg.WriteAsync(tempFile2);
                jpeg.Dispose();
                System.IO.File.Delete(tempFile);
                tempFile = tempFile2;
                using var fileStream2 = System.IO.File.OpenRead(tempFile);

                List<VisualFeatureTypes?> visualFeatureTypes = new List<VisualFeatureTypes?>()
                {
                    VisualFeatureTypes.Tags,
                    VisualFeatureTypes.Adult
                };

                ImageAnalysis imageAnalysisResults = await computerVisionClient.AnalyzeImageInStreamAsync(fileStream2, visualFeatureTypes);
                fileStream2.Dispose();

                if (imageAnalysisResults.Adult.IsAdultContent || imageAnalysisResults.Adult.IsGoryContent || imageAnalysisResults.Adult.IsRacyContent)
                {
                    return BadRequest("Error: Photo does not pass content check.");
                }

                StringBuilder randomFileNameBuilder = new StringBuilder();
                for (int i = 0; i < 32; i++)
                {
                    // Create 32 character filename with chars between a-z.
                    char randomChar = (char)RandomNumberGenerator.GetInt32(97, 123);
                    randomFileNameBuilder.Append(randomChar);
                }

                string randomFileName = randomFileNameBuilder.ToString();

                var crossStreetItem = await ReverseSearchCrossStreet(photoLocation, mapsSearchClient);
                if (crossStreetItem == null)
                {
                    return StatusCode((int)HttpStatusCode.InternalServerError, "Error: Could not determine cross street.");
                }

                string crossStreet = crossStreetItem.Address.StreetName;

                InitialPhotoUploadMetadata metadata = new InitialPhotoUploadMetadata(randomFileName,
                    photoDate.Value,
                    photoLocation.Latitude.ToString("#.#####"),
                    photoLocation.Longitude.ToString("#.#####"),
                    crossStreet,
                    imageAnalysisResults.Tags.ToList());

                BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{randomFileName}.jpeg");
                using var fileStream3 = System.IO.File.OpenRead(tempFile);
                await photoBlobClient.UploadAsync(fileStream3);
                fileStream3.Dispose();

                BlobClient metadataBlobClient = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{randomFileName}.json");
                await metadataBlobClient.UploadAsync(new BinaryData(metadata));

                Uri sasUri = await photoBlobClient.GenerateUserDelegationReadOnlySasUri(DateTimeOffset.UtcNow.AddMinutes(10));
                return Ok(InitialPhotoUploadWithSasUriMetadata.FromMetadata(sasUri.ToString(), metadata));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failure during photo upload");
                return StatusCode((int)HttpStatusCode.InternalServerError, "Failure during photo upload.");
            }
            finally
            {
                System.IO.File.Delete(tempFile);
            }
        }

        [HttpPost("Finalize")]
        public async Task FinalizeUpload([FromBody] FinalizedPhotoUploadMetadata metadata)
        {
            if (metadata.NumberOfCars == null || metadata.NumberOfCars < 1)
            {
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (metadata.Attribute != null && metadata.Attribute.Value)
            {
                if (!string.IsNullOrWhiteSpace(metadata.TwitterAccessToken))
                {
                    OAuth2Authorizer userAuth = new OAuth2Authorizer()
                    {
                        CredentialStore = new OAuth2CredentialStore()
                        {
                            AccessToken = metadata.TwitterAccessToken
                        }
                    };
                    TwitterContext twitterContext = new TwitterContext(userAuth);
                    try
                    {
                        TwitterUserQuery? response = await (from user in twitterContext.TwitterUser
                                                            where user.Type == UserType.Me
                                                            select user).SingleOrDefaultAsync();
                        TwitterUser? twitterUser = response?.Users?.SingleOrDefault();
                        if (twitterUser != null)
                        {
                            if (twitterUser.Username != null && metadata.TwitterUsername != null &&
                                twitterUser.Username != metadata.TwitterUsername)
                            {
                                logger.LogWarning($"Metadata Twitter username dosen't match authenticated user." +
                                    $"Metadata username: {metadata.TwitterUsername}. Auth username: {twitterUser.Username}.");
                                metadata.TwitterSubmittedBy = "Submission";
                                metadata.Attribute = false;
                                metadata.TwitterUsername = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to verify authenticated Twitter user.");
                        metadata.TwitterSubmittedBy = "Submission";
                        metadata.Attribute = false;
                        metadata.TwitterUsername = null;
                    }
                }

                if (!string.IsNullOrWhiteSpace(metadata.MastodonEndpoint) && !string.IsNullOrWhiteSpace(metadata.MastodonAccessToken))
                {
                    Uri endpoint = new Uri(metadata.MastodonEndpoint);
                    MastodonClient mastodonClient = await mastodonClientProvider.GetUserClient(endpoint, metadata.MastodonAccessToken);
                    MastodonAccount mastodonAccount = await mastodonClient.VerifyCredentials();
                    string mastodonUsername = $"@{mastodonAccount.Username}@{endpoint.Host}";
                    if (mastodonUsername != metadata.MastodonFullUsername)
                    {
                        logger.LogWarning($"Metadata Mastodon username doesn't match authenticated user." +
                            $"Metadata username: {metadata.MastodonFullUsername}. Auth username: {mastodonUsername}.");
                        metadata.MastodonSubmittedBy = "Submission";
                        metadata.Attribute = false;
                        metadata.MastodonUsername = null;
                        metadata.MastodonFullUsername = null;
                    }
                }
            }

            metadata.TwitterAccessToken = null;
            metadata.MastodonAccessToken = null;
            string randomFileName = metadata.PhotoId;
            BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{randomFileName}.jpeg");
            await photoBlobClient.Move($"{FinalizedUploadPrefix}{randomFileName}.jpeg");

            BlobClient newMetadataBlobClient = blobContainerClient.GetBlobClient($"{FinalizedUploadPrefix}{randomFileName}.json");
            await newMetadataBlobClient.UploadAsync(new BinaryData(metadata));

            BlobClient metadataBlobClient = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{randomFileName}.json");
            await metadataBlobClient.DeleteAsync();

            // Ping me on Slack about the new submission
            await slackbotProvider.SendSlackMessage($"New submission. {metadata.NumberOfCars} {helperMethods.GetCarsString(metadata.NumberOfCars.Value)} " +
                $"@ {metadata.PhotoCrossStreet} submitted {DateTime.Now:s}");

            Response.StatusCode = (int)HttpStatusCode.NoContent;
        }

        private async Task<ReverseSearchCrossStreetAddressResultItem?> ReverseSearchCrossStreet(Position position, MapsSearchClient mapsSearchClient)
        {
            ReverseSearchCrossStreetOptions options = new ReverseSearchCrossStreetOptions()
            {
                Coordinates = new Azure.Core.GeoJson.GeoPosition(position.Longitude, position.Latitude),
            };
            var response = await mapsSearchClient.ReverseSearchCrossStreetAddressAsync(options);
            if (response == null || response.Value == null || response.Value.Addresses == null || response.Value.Addresses.Count == 0)
            {
                if (response != null)
                {
                    var rawResponse = response.GetRawResponse();
                    logger.LogError($"Failed to reverse search cross street address. Is error: {rawResponse.IsError}. Status code: {rawResponse.Status}. Reason phrase: {rawResponse.ReasonPhrase}.");
                }
                return null;
            }

            var item = response.Value.Addresses[0];
            return item;
        }

        private DateTime? GetPhotoDate(string path)
        {
            try
            {
                string output = CallExifTool(path, "-s -s -s -createdate");
                if (string.IsNullOrWhiteSpace(output))
                {
                    return null;
                }

                string[] splitOutput = output.Split(' ');
                if (splitOutput.Length != 2)
                {
                    logger.LogWarning($"GetPhotoDate() split output was {splitOutput.Length}. Expected 2. Output: {output}");
                    return null;
                }

                bool parsedDate = DateOnly.TryParse(splitOutput[0].Replace(":", "-"), out DateOnly date);
                if (!parsedDate)
                {
                    logger.LogWarning($"GetPhotoDate() could not parse date. Output: {output}");
                    return null;
                }

                bool parsedTime = TimeOnly.TryParse(splitOutput[1], out TimeOnly time);
                if (!parsedTime)
                {
                    logger.LogWarning($"GetPhotoDate() could not parse time. Output: {output}");
                    return null;
                }

                return date.ToDateTime(time);
            }
            catch
            {
                return null;
            }
        }

        private Position? GetPhotoLocation(string path)
        {
            try
            {
                string output = CallExifTool(path, "-n -s -s -s -gpsposition");
                if (string.IsNullOrWhiteSpace(output))
                {
                    return null;
                }

                string[] splitOutput = output.Split(' ');
                if (splitOutput.Length != 2)
                {
                    logger.LogWarning($"GetPhotoLocation() split output was {splitOutput.Length}. Expected 2. Output: {output}");
                    return null;
                }

                bool parsedLatitude = double.TryParse(splitOutput[0], out double latitude);
                if (!parsedLatitude)
                {
                    logger.LogWarning($"GetPhotoLocation() could not parse latitude. Output: {output}");
                    return null;
                }

                bool parsedLongitude = double.TryParse(splitOutput[1], out double longitude);
                if (!parsedLongitude)
                {
                    logger.LogWarning($"GetPhotoLocation() could not parse longitude. Output: {output}");
                    return null;
                }

                return new Position(longitude, latitude);
            }
            catch
            {
                return null;
            }
        }

        private string CallExifTool(string path, string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo("exiftool/exiftool.exe", $"{arguments} {path}");
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using Process? process = Process.Start(info);
            if (process == null)
            {
                logger.LogError("exiftool.exe didn't start for some reason.");
                return string.Empty;
            }
            string output = process.StandardOutput.ReadToEnd();
            string errorOutput = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(5));
            if (process.ExitCode != 0)
            {
                // error
                logger.LogInformation($"Could not get EXIF data for photo. Error: {errorOutput}");
                throw new Exception(errorOutput);
            }
            else
            {
                return output.Replace("\r\n", "");
            }
        }

        public class InitialPhotoUploadWithSasUriMetadata : InitialPhotoUploadMetadata
        {
            public string Uri { get; set; }
            
            public InitialPhotoUploadWithSasUriMetadata(string uri,
                string photoId,
                DateTime photoDateTime,
                string photoLatitude,
                string photoLongitude,
                string photoCrossStreet,
                List<ImageTag> tags) :
                base(photoId,
                    photoDateTime,
                    photoLatitude,
                    photoLongitude,
                    photoCrossStreet,
                    tags)
            {
                Uri = uri;
            }

            public static InitialPhotoUploadWithSasUriMetadata FromMetadata(string uri, InitialPhotoUploadMetadata metadata)
            {
                return new InitialPhotoUploadWithSasUriMetadata(uri,
                    metadata.PhotoId,
                    metadata.PhotoDateTime,
                    metadata.PhotoLatitude,
                    metadata.PhotoLongitude,
                    metadata.PhotoCrossStreet,
                    metadata.Tags);
            }
        }
    }
}
