using System.Net;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Imgur.API.Endpoints;
using LinqToTwitter;
using LinqToTwitter.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Newtonsoft.Json;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("[controller]")]
    [ApiController]
    [Authorize]
    public class AdminPageController : ControllerBase
    {
        private readonly ILogger<AdminPageController> logger;
        private readonly BlobContainerClient blobContainerClient;
        private readonly TwitterContext uploadTwitterContext;
        private readonly ImageEndpoint imgurImageEndpoint;
        private readonly ReportedItemsDatabase reportedItemsDatabase;

        public AdminPageController(ILogger<AdminPageController> logger,
            BlobContainerClient blobContainerClient,
            SecretClient secretClient,
            ImageEndpoint imgurImageEndpoint,
            ReportedItemsDatabase reportedItemsDatabase)
        {
            this.logger = logger;
            this.blobContainerClient = blobContainerClient;
            this.imgurImageEndpoint = imgurImageEndpoint;
            this.reportedItemsDatabase = reportedItemsDatabase;

            SingleUserAuthorizer auth = new SingleUserAuthorizer()
            {
                CredentialStore = new SingleUserInMemoryCredentialStore()
                {
                    ConsumerKey = secretClient.GetSecret("twitter-consumer-key").Value.Value,
                    ConsumerSecret = secretClient.GetSecret("twitter-consumer-key-secret").Value.Value,
                    AccessToken = secretClient.GetSecret("twitter-oauth1-access-token").Value.Value,
                    AccessTokenSecret = secretClient.GetSecret("twitter-oauth1-access-token-secret").Value.Value
                }
            };
            uploadTwitterContext = new TwitterContext(auth);
        }

        [HttpGet]
        public IActionResult Get()
        {
            return File("admin.html", "text/html");
        }

        [HttpGet("/api/AdminPage/PendingPhotos")]
        public async Task<List<FinalizedPhotoUploadWithSasUriMetadata>> GetPendingPhotos()
        {
            List<FinalizedPhotoUploadWithSasUriMetadata> photos = new List<FinalizedPhotoUploadWithSasUriMetadata>();
            var blobs = blobContainerClient.GetBlobsAsync(prefix: UploadController.FinalizedUploadPrefix);
            await foreach (var blob in blobs)
            {
                if (blob.Name.EndsWith(".jpeg"))
                {
                    // Skip pictures because we'll load them once we have the JSON
                    continue;
                }
                BlobClient jsonBlobClient = blobContainerClient.GetBlobClient(blob.Name);
                var downloadResponse = await jsonBlobClient.DownloadContentAsync();
                var metadata = JsonConvert.DeserializeObject<FinalizedPhotoUploadWithSasUriMetadata>(downloadResponse.Value.Content.ToString());
                BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata.PhotoId}.jpeg");
                Uri photoUri = await photoBlobClient.GenerateUserDelegationReadOnlySasUri(DateTimeOffset.UtcNow.AddHours(1));
                metadata.Uri = photoUri.ToString();
                photos.Add(metadata);
            }

            return photos;
        }

        [HttpPost("/api/AdminPage/UploadTweet")]
        public async Task<IActionResult> UploadTweet([FromBody] FinalizedPhotoUploadMetadata metadata)
        {
            string carsString;
            if (metadata.NumberOfCars == 1)
            {
                carsString = "car";
            }
            else
            {
                carsString = "cars";
            }

            string tweetBody = $"{metadata.NumberOfCars} {carsString}\n" +
                $"Date: {metadata.PhotoDateTime.ToString("M/d/yyyy")}\n" +
                $"Time: {metadata.PhotoDateTime.ToString("h:mm tt")}\n" +
                $"Location: {metadata.PhotoCrossStreet}\n" +
                $"GPS: {metadata.PhotoLatitude}, {metadata.PhotoLongitude}\n" +
                $"{metadata.SubmittedBy}";

            BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata.PhotoId}.jpeg");
            var photoDownload = await photoBlobClient.DownloadContentAsync();
            var photoBytes = photoDownload.Value.Content.ToArray();

            var imgurUpload = await imgurImageEndpoint.UploadImageAsync(new MemoryStream(photoBytes));

            Media? twitterMedia = await uploadTwitterContext.UploadMediaAsync(photoBytes, "image/jpg", "tweet_image");
            if (twitterMedia == null)
            {
                string error = $"Failed to upload tweet image for {metadata.PhotoId}.";
                logger.LogError(error);
                return StatusCode((int)HttpStatusCode.InternalServerError, error);
            }

            Tweet? tweet = await uploadTwitterContext.TweetMediaAsync(tweetBody, new List<string>() { twitterMedia.MediaID.ToString() });
            if (tweet == null)
            {
                string error = $"Failed to send tweet for {metadata.PhotoId}.";
                logger.LogError(error);
                return StatusCode((int)HttpStatusCode.InternalServerError, error);
            }

            ReportedItem? latestItem = await reportedItemsDatabase.GetLatestReportedItem();
            if (latestItem == null)
            {
                string error = "Failed to get latest reported item from database when uploading photo.";
                logger.LogError(error);
                return StatusCode((int)HttpStatusCode.InternalServerError, error);
            }

            latestItem.Latest = false;
            bool updatedLatest = await reportedItemsDatabase.UpdateReportedItem(latestItem);
            if (!updatedLatest)
            {
                logger.LogError($"Failed to update latest tweet");
            }

            ReportedItem newReportedItem = new ReportedItem()
            {
                TweetId = $"{tweet.ID}.0",
                CreatedAt = DateTime.UtcNow,
                NumberOfCars = metadata.NumberOfCars!.Value,
                Date = DateOnly.FromDateTime(metadata.PhotoDateTime),
                Time = TimeOnly.FromDateTime(metadata.PhotoDateTime),
                LocationString = metadata.PhotoCrossStreet,
                Location = new Microsoft.Azure.Cosmos.Spatial.Point(double.Parse(metadata.PhotoLongitude), double.Parse(metadata.PhotoLatitude)),
                Latest = true
            };
            newReportedItem.ImgurUrls.Add(imgurUpload.Link);
            bool addedToDatabase = await reportedItemsDatabase.AddReportedItem(newReportedItem);
            if (!addedToDatabase)
            {
                logger.LogError($"Failed to add tweet to database: {newReportedItem.TweetId}");
            }

            BlobClient metadataBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata.PhotoId}.json");
            await photoBlobClient.DeleteAsync();
            await metadataBlobClient.DeleteAsync();

            return NoContent();
        }

        [HttpDelete("/api/AdminPage/DeletePendingPhoto")]
        public async Task DeletePendingPhoto([FromBody] FinalizedPhotoUploadMetadata metadata)
        {
            BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata.PhotoId}.jpeg");
            BlobClient metadataBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata.PhotoId}.json");
            await photoBlobClient.DeleteAsync();
            await metadataBlobClient.DeleteAsync();
            Response.StatusCode = (int)HttpStatusCode.NoContent;
        }

        [HttpGet("/api/AdminPage/test")]
        public int Random()
        {
            Random random = new Random();
            return random.Next();
        }

        public class FinalizedPhotoUploadWithSasUriMetadata : FinalizedPhotoUploadMetadata
        {
            public string? Uri { get; set; }

            public FinalizedPhotoUploadWithSasUriMetadata(int numberOfCars,
                string photoId,
                DateTime photoDateTime,
                string photoLatitude,
                string photoLongitude,
                string photoCrossStreet,
                List<ImageTag> tags,
                string submittedBy = "Submission",
                bool? attribute = null,
                string? twitterUsername = null,
                string? twitterAccessToken = null) :
                base(numberOfCars,
                    photoId,
                    photoDateTime,
                    photoLatitude,
                    photoLongitude,
                    photoCrossStreet,
                    tags,
                    submittedBy,
                    attribute,
                    twitterUsername,
                    twitterAccessToken)
            {
            }
        }
    }
}
