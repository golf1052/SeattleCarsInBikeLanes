﻿using System.Diagnostics;
using System.Text;
using Azure.AI.ContentSafety;
using Azure.AI.Vision.ImageAnalysis;
using Azure.Maps.Search;
using Azure.Maps.Search.Models;
using Azure.Storage.Blobs;
using golf1052.atproto.net;
using golf1052.Mastodon;
using golf1052.Mastodon.Models.Accounts;
using ImageMagick;
using LinqToTwitter;
using LinqToTwitter.OAuth;
using Microsoft.AspNetCore.Mvc;
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
        private readonly ImageAnalysisClient imageAnalysisClient;
        private readonly ContentSafetyClient contentSafetyClient;
        private readonly MapsSearchClient mapsSearchClient;
        private readonly BlobContainerClient blobContainerClient;
        private readonly MastodonClientProvider mastodonClientProvider;
        private readonly SlackbotProvider slackbotProvider;
        private readonly HelperMethods helperMethods;

        public UploadController(ILogger<UploadController> logger,
            ImageAnalysisClient imageAnalysisClient,
            ContentSafetyClient contentSafetyClient,
            MapsSearchClient mapsSearchClient,
            BlobContainerClient blobContainerClient,
            MastodonClientProvider mastodonClientProvider,
            SlackbotProvider slackbotProvider,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.imageAnalysisClient = imageAnalysisClient;
            this.contentSafetyClient = contentSafetyClient;
            this.mapsSearchClient = mapsSearchClient;
            this.blobContainerClient = blobContainerClient;
            this.mastodonClientProvider = mastodonClientProvider;
            this.slackbotProvider = slackbotProvider;
            this.helperMethods = helperMethods;
        }

        [HttpPost("Initial")]
        public async Task<IActionResult> UploadPhoto([FromForm]List<IFormFile> files)
        {
            if (Request.ContentLength == 0 || files == null || files.Count == 0)
            {
                string error = "Error: No photo uploaded.";
                logger.LogError(error);
                return BadRequest(error);
            }

            if (files.Count > 4)
            {
                string error = "Cannot upload more than 4 images on a report";
                logger.LogError(error);
                return BadRequest(error);
            }

            string submissionId = helperMethods.GetRandomFileName();

            List<Task<InitialPhotoUploadWithSasUriMetadata>> metadataTasks = new List<Task<InitialPhotoUploadWithSasUriMetadata>>();
            List<InitialPhotoUploadWithSasUriMetadata> metadata = new List<InitialPhotoUploadWithSasUriMetadata>();
            Dictionary<int, string> exceptions = new Dictionary<int, string>();
            for (int i = 0; i < files.Count; i++)
            {
                IFormFile? file = files[i];
                try
                {
                    metadataTasks.Add(ProcessInitialUpload(file, submissionId, i));
                }
                catch (Exception ex)
                {
                    exceptions.Add(i, ex.Message);
                }
            }

            await Task.WhenAll(metadataTasks);

            for (int i = 0; i < metadataTasks.Count; i++)
            {
                var metadataTask = metadataTasks[i];
                if (metadataTask.IsFaulted)
                {
                    if (metadataTask.Exception != null)
                    {
                        if (exceptions.ContainsKey(i))
                        {
                            exceptions[i] += $"\n{metadataTask.Exception.Message}";
                        }
                        else
                        {
                            exceptions.Add(i, metadataTask.Exception.Message);
                        }
                    }
                }
                else
                {
                    metadata.Add(metadataTask.Result);
                }
            }

            if (exceptions.Count > 0)
            {
                // return a bad request with a string containing each exception and the photo number on a new line
                StringBuilder requestBuilder = new StringBuilder();
                foreach (var exception in exceptions)
                {
                    requestBuilder.AppendLine($"Photo {exception.Key + 1}: {exception.Value}");
                }
                string requestString = requestBuilder.ToString();
                logger.LogError(requestString);
                return BadRequest(requestString);
            }

            return Ok(metadata);
        }

        private async Task<InitialPhotoUploadWithSasUriMetadata> ProcessInitialUpload(IFormFile file, string submissionId, int index)
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                using var fileStream1 = System.IO.File.OpenWrite(tempFile);
                await file.CopyToAsync(fileStream1);
                fileStream1.Dispose();
                DateTime? photoDate = GetPhotoDate(tempFile);
                Position? photoLocation = GetPhotoLocation(tempFile);
                if (photoLocation != null && !SeattleBoundingBox.Contains(photoLocation))
                {
                    string lat = photoLocation.Latitude.ToString("#.#####");
                    string lon = photoLocation.Longitude.ToString("#.#####");
                    throw new BikeLaneException($"Error: Photo not taken in Seattle. The location on the photo is " +
                        $"<a href=\"https://bing.com/maps?cp={photoLocation.Latitude}~{photoLocation.Longitude}&lvl=13&sp=point.{photoLocation.Latitude}_{photoLocation.Longitude}_Photo%20location___\" target=\"_blank\">{lat}, {lon}</a>");
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

                Task<Azure.Response<AnalyzeImageResult>> contentSafetyAnalyzeImageTask = contentSafetyClient.AnalyzeImageAsync(BinaryData.FromStream(fileStream2));
                fileStream2.Seek(0, SeekOrigin.Begin);
                Task<Azure.Response<ImageAnalysisResult>> analyzeImageTask = imageAnalysisClient.AnalyzeAsync(BinaryData.FromStream(fileStream2), VisualFeatures.Tags);

                Task<ReverseSearchCrossStreetAddressResultItem?>? crossStreetItemTask = null;
                if (photoLocation != null)
                {
                    crossStreetItemTask = helperMethods.ReverseSearchCrossStreet(photoLocation, mapsSearchClient);
                }

                // No idea why this is not detected as null free since we're doing the filter
                await Task.WhenAll(new List<Task?>() { crossStreetItemTask, contentSafetyAnalyzeImageTask, analyzeImageTask }.Where(t => t != null)!);

                fileStream2.Dispose();

                AnalyzeImageResult contentSafetyAnalyzeImageResult = contentSafetyAnalyzeImageTask.Result.Value;
                ImageCategoriesAnalysis? hateCategory = contentSafetyAnalyzeImageResult.CategoriesAnalysis.FirstOrDefault(c => c.Category == ImageCategory.Hate);
                ImageCategoriesAnalysis? selfHarmCategory = contentSafetyAnalyzeImageResult.CategoriesAnalysis.FirstOrDefault(c => c.Category == ImageCategory.SelfHarm);
                ImageCategoriesAnalysis? sexualCategory = contentSafetyAnalyzeImageResult.CategoriesAnalysis.FirstOrDefault(c => c.Category == ImageCategory.Sexual);
                ImageCategoriesAnalysis? violenceCategory = contentSafetyAnalyzeImageResult.CategoriesAnalysis.FirstOrDefault(c => c.Category == ImageCategory.Violence);
                if ((hateCategory != null && hateCategory.Severity > 2) ||
                    (selfHarmCategory != null && selfHarmCategory.Severity > 2) ||
                    (sexualCategory != null && sexualCategory.Severity > 2) ||
                    (violenceCategory != null && violenceCategory.Severity > 2))
                {
                    logger.LogWarning($"Photo does not pass content check. Analysis results: " +
                    $"Hate: {hateCategory?.Severity}, Self Harm: {selfHarmCategory?.Severity}, Sexual: {sexualCategory?.Severity}, Violence: {violenceCategory?.Severity}");
                    throw new BikeLaneException("Error: Photo does not pass content check.");
                }

                ImageAnalysisResult analyzeImageResult = analyzeImageTask.Result.Value;
                static List<ImageTag> AzureTagToImageTag(IReadOnlyList<DetectedTag> tags)
                {
                    List<ImageTag> imageTags = new List<ImageTag>();
                    foreach (var tag in tags)
                    {
                        imageTags.Add(new ImageTag()
                        {
                            Name = tag.Name,
                            Confidence = tag.Confidence
                        });
                    }
                    return imageTags;
                }

                ReverseSearchCrossStreetAddressResultItem? crossStreetItem = null;
                string? crossStreet = null;
                if (crossStreetItemTask != null)
                {
                    crossStreetItem = crossStreetItemTask.Result;
                    if (crossStreetItem == null)
                    {
                        throw new BikeLaneException("Error: Could not determine cross street.");
                    }
                    else
                    {
                        crossStreet = crossStreetItem.Address.StreetName;
                    }
                }

                string randomFileName = helperMethods.GetRandomFileName();
                InitialPhotoUploadMetadata metadata;
                if (photoDate != null && photoLocation != null && crossStreet != null)
                {
                    metadata = new InitialPhotoUploadMetadata(randomFileName,
                        submissionId,
                        index,
                        photoDate.Value,
                        photoLocation.Latitude.ToString("#.#####"),
                        photoLocation.Longitude.ToString("#.#####"),
                        crossStreet,
                        AzureTagToImageTag(analyzeImageResult.Tags.Values));
                }
                else
                {
                    metadata = new InitialPhotoUploadMetadata(randomFileName,
                        submissionId,
                        index,
                        AzureTagToImageTag(analyzeImageResult.Tags.Values));
                }

                BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{randomFileName}.jpeg");
                using var fileStream3 = System.IO.File.OpenRead(tempFile);
                Task uploadPhotoTask = photoBlobClient.UploadAsync(fileStream3);

                BlobClient metadataBlobClient = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{randomFileName}.json");
                Task uploadMetadataTask = metadataBlobClient.UploadAsync(new BinaryData(metadata));

                await Task.WhenAll(uploadPhotoTask, uploadMetadataTask);
                fileStream3.Dispose();

                Uri sasUri = await photoBlobClient.GenerateUserDelegationReadOnlySasUri(DateTimeOffset.UtcNow.AddMinutes(10));
                return InitialPhotoUploadWithSasUriMetadata.FromMetadata(sasUri.ToString(), metadata);
            }
            catch (BikeLaneException ex)
            {
                logger.LogError(ex, "Failure during photo upload.");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failure during photo upload");
                throw new Exception("Failure during photo upload.");
            }
            finally
            {
                System.IO.File.Delete(tempFile);
            }
        }

        [HttpPost("Finalize")]
        public async Task<IActionResult> FinalizeUpload([FromBody] List<FinalizedPhotoUploadMetadata> data)
        {
            if (data.Count == 0)
            {
                string error = "Expected at least 1 metadata object";
                logger.LogError(error);
                return BadRequest(error);
            }

            FinalizedPhotoUploadMetadata metadata = data[0];
            if (!metadata.PhotoDateTime.HasValue)
            {
                string error = "Please select the date and time the report happened.";
                logger.LogError(error);
                return BadRequest(error);
            }
            if (string.IsNullOrWhiteSpace(metadata.PhotoLatitude) || string.IsNullOrEmpty(metadata.PhotoLongitude))
            {
                string error = "Please select the location the report happened.";
                logger.LogError(error);
                return BadRequest(error);
            }
            Position photoLocation = new Position(double.Parse(metadata.PhotoLongitude!), double.Parse(metadata.PhotoLatitude!));
            if (!SeattleBoundingBox.Contains(photoLocation))
            {
                string error = "Photo not taken in Seattle.";
                logger.LogError(error);
                return BadRequest(error);
            }

            if (string.IsNullOrWhiteSpace(metadata.PhotoCrossStreet))
            {
                ReverseSearchCrossStreetAddressResultItem? crossStreetItem = null;
                crossStreetItem = await helperMethods.ReverseSearchCrossStreet(photoLocation, mapsSearchClient);
                if (crossStreetItem == null)
                {
                    logger.LogWarning($"Couldn't find cross street for {metadata.PhotoLatitude}, {metadata.PhotoLongitude}");
                }
                else
                {
                    foreach (var d in data)
                    {
                        d.PhotoCrossStreet = crossStreetItem.Address.StreetName;
                    }
                }
            }

            if (metadata.NumberOfCars == null || metadata.NumberOfCars < 1)
            {
                string error = "Number of cars must be at least 1.";
                logger.LogError(error);
                return BadRequest(error);
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

                                foreach (var d in data)
                                {
                                    d.TwitterSubmittedBy = "Submission";
                                    d.Attribute = false;
                                    d.TwitterUsername = null;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to verify authenticated Twitter user.");
                        foreach (var d in data)
                        {
                            d.TwitterSubmittedBy = "Submission";
                            d.Attribute = false;
                            d.TwitterUsername = null;
                        }
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
                        foreach (var d in data)
                        {
                            d.MastodonSubmittedBy = "Submission";
                            d.Attribute = false;
                            d.MastodonUsername = null;
                            d.MastodonFullUsername = null;
                        }
                    }
                }

                bool? verifiedBlueskyUser = await VerifyBlueskyUser(metadata);
                if (verifiedBlueskyUser.HasValue && !verifiedBlueskyUser.Value)
                {
                    logger.LogWarning("Failed to verify Bluesky user.");
                    foreach (var d in data)
                    {
                        d.BlueskySubmittedBy = "Submission";
                        d.Attribute = false;
                        d.BlueskyHandle = null;
                        d.BlueskyUserDid = null;
                    }
                }
            }

            foreach (var d in data)
            {
                // Clear all tokens and other sensitive info before saving to Azure
                d.TwitterAccessToken = null;
                d.MastodonAccessToken = null;
                d.ThreadsAccessToken = null;
                d.BlueskyUserKeyId = null;
                d.BlueskyUserPrivateKey = null;
                d.BlueskyUserBaseUrl = null;
                d.BlueskyUserAccessToken = null;

                string randomFileName = d.PhotoId;
                BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{randomFileName}.jpeg");
                await photoBlobClient.Move($"{FinalizedUploadPrefix}{randomFileName}.jpeg");

                BlobClient newMetadataBlobClient = blobContainerClient.GetBlobClient($"{FinalizedUploadPrefix}{randomFileName}.json");
                await newMetadataBlobClient.UploadAsync(new BinaryData(d));

                BlobClient metadataBlobClient = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{randomFileName}.json");
                await metadataBlobClient.DeleteAsync();
            }

            // Ping me on Slack about the new submission
            await slackbotProvider.SendSlackMessage($"New submission. {metadata.NumberOfCars} {helperMethods.GetCarsString(metadata.NumberOfCars.Value)} " +
                $"@ {metadata.PhotoCrossStreet} submitted {DateTime.Now:s}");

            return NoContent();
        }

        public async Task<bool?> VerifyBlueskyUser(FinalizedPhotoUploadMetadata metadata)
        {
            if (!string.IsNullOrWhiteSpace(metadata.BlueskyHandle) &&
                !string.IsNullOrWhiteSpace(metadata.BlueskyUserDid) &&
                !string.IsNullOrWhiteSpace(metadata.BlueskyUserKeyId) &&
                !string.IsNullOrWhiteSpace(metadata.BlueskyUserPrivateKey) &&
                !string.IsNullOrWhiteSpace(metadata.BlueskyUserBaseUrl) &&
                !string.IsNullOrWhiteSpace(metadata.BlueskyUserAccessToken))
            {
                try
                {
                    AtProtoOAuthClient blueskyClient = new AtProtoOAuthClient(metadata.BlueskyUserDid,
                        metadata.BlueskyUserKeyId,
                        metadata.BlueskyUserPrivateKey,
                        metadata.BlueskyUserBaseUrl,
                        metadata.BlueskyUserAccessToken);
                    var profile = await blueskyClient.GetProfile();
                    return profile.Handle == metadata.BlueskyHandle &&
                        profile.Did == metadata.BlueskyUserDid;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to verify Bluesky user.");
                    return false;
                }
            }
            else
            {
                return null;
            }
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
                logger.LogError($"Could not get EXIF data for photo. Error: {errorOutput}");
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
                string submissionId,
                int photoNumber,
                DateTime photoDateTime,
                string photoLatitude,
                string photoLongitude,
                string photoCrossStreet,
                List<ImageTag> tags) :
                base(photoId,
                    submissionId,
                    photoNumber,
                    photoDateTime,
                    photoLatitude,
                    photoLongitude,
                    photoCrossStreet,
                    tags)
            {
                Uri = uri;
            }

            public InitialPhotoUploadWithSasUriMetadata(string uri,
                string photoId,
                string submissionId,
                int photoNumber,
                List<ImageTag> tags) :
                base(photoId,
                    submissionId,
                    photoNumber,
                    tags)
            {
                Uri = uri;
            }

            public static InitialPhotoUploadWithSasUriMetadata FromMetadata(string uri, InitialPhotoUploadMetadata metadata)
            {
                if (metadata.PhotoDateTime.HasValue &&
                    !string.IsNullOrWhiteSpace(metadata.PhotoLatitude) &&
                    !string.IsNullOrWhiteSpace(metadata.PhotoLongitude) &&
                    !string.IsNullOrWhiteSpace(metadata.PhotoCrossStreet))
                {
                    return new InitialPhotoUploadWithSasUriMetadata(uri,
                        metadata.PhotoId,
                        metadata.SubmissionId,
                        metadata.PhotoNumber,
                        metadata.PhotoDateTime.Value,
                        metadata.PhotoLatitude,
                        metadata.PhotoLongitude,
                        metadata.PhotoCrossStreet,
                        metadata.Tags);
                }
                else
                {
                    return new InitialPhotoUploadWithSasUriMetadata(uri,
                        metadata.PhotoId,
                        metadata.SubmissionId,
                        metadata.PhotoNumber,
                        metadata.Tags);
                }
            }
        }
    }
}
