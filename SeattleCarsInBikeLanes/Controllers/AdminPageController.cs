using System.Net;
using Azure.Maps.Search;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using golf1052.Mastodon;
using golf1052.Mastodon.Models.Statuses;
using golf1052.Mastodon.Models.Statuses.Media;
using Imgur.API.Endpoints;
using Imgur.API.Models;
using LinqToTwitter;
using LinqToTwitter.Common;
using LinqToTwitter.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Newtonsoft.Json;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("[controller]")]
    [ApiController]
    [Authorize]
    public class AdminPageController : ControllerBase
    {
        private readonly ILogger<AdminPageController> logger;
        private readonly HelperMethods helperMethods;
        private readonly BlobContainerClient blobContainerClient;
        private readonly TwitterContext uploadTwitterContext;
        private readonly ImageEndpoint imgurImageEndpoint;
        private readonly ReportedItemsDatabase reportedItemsDatabase;
        private readonly HttpClient httpClient;
        private readonly MapsSearchClient mapsSearchClient;
        private readonly MastodonClientProvider mastodonClientProvider;
        private readonly FeedProvider feedProvider;

        public AdminPageController(ILogger<AdminPageController> logger,
            HelperMethods helperMethods,
            BlobContainerClient blobContainerClient,
            SecretClient secretClient,
            ImageEndpoint imgurImageEndpoint,
            ReportedItemsDatabase reportedItemsDatabase,
            HttpClient httpClient,
            MapsSearchClient mapsSearchClient,
            MastodonClientProvider mastodonClientProvider,
            FeedProvider feedProvider)
        {
            this.logger = logger;
            this.helperMethods = helperMethods;
            this.blobContainerClient = blobContainerClient;
            this.imgurImageEndpoint = imgurImageEndpoint;
            this.reportedItemsDatabase = reportedItemsDatabase;
            this.httpClient = httpClient;
            this.mapsSearchClient = mapsSearchClient;
            this.mastodonClientProvider = mastodonClientProvider;
            this.feedProvider = feedProvider;

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
                BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata!.PhotoId}.jpeg");
                Uri photoUri = await photoBlobClient.GenerateUserDelegationReadOnlySasUri(DateTimeOffset.UtcNow.AddHours(1));
                metadata.Uri = photoUri.ToString();
                photos.Add(metadata);
            }

            return photos;
        }

        [HttpPost("/api/AdminPage/UploadTweet")]
        public async Task<IActionResult> UploadTweet([FromBody] FinalizedPhotoUploadMetadata metadata)
        {
            MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();
            string carsString;
            if (metadata.NumberOfCars == 1)
            {
                carsString = "car";
            }
            else
            {
                carsString = "cars";
            }

            string postBody = $"{metadata.NumberOfCars} {carsString}\n" +
                $"Date: {metadata.PhotoDateTime.ToString("M/d/yyyy")}\n" +
                $"Time: {metadata.PhotoDateTime.ToString("h:mm tt")}\n" +
                $"Location: {metadata.PhotoCrossStreet}\n" +
                $"GPS: {metadata.PhotoLatitude}, {metadata.PhotoLongitude}";

            string tweetBody = postBody;
            if (!string.IsNullOrEmpty(metadata.TwitterSubmittedBy))
            {
                if (metadata.TwitterSubmittedBy.StartsWith("Submitted by"))
                {
                    tweetBody += $"\n{metadata.TwitterSubmittedBy}";
                }
                else if (!string.IsNullOrWhiteSpace(metadata.MastodonSubmittedBy) &&
                    metadata.MastodonSubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.MastodonEndpoint))
                {
                    Uri mastodonEndpoint = new Uri(metadata.MastodonEndpoint);
                    tweetBody += $"\nSubmitted by https://{mastodonEndpoint.Host}/@{metadata.MastodonUsername}";
                }
                else
                {
                    tweetBody += $"\n{metadata.TwitterSubmittedBy}";
                }
            }
            else
            {
                tweetBody += $"\nSubmission";
            }

            string tootBody = postBody;
            if (!string.IsNullOrWhiteSpace(metadata.MastodonSubmittedBy))
            {
                if (metadata.MastodonSubmittedBy.StartsWith("Submitted by"))
                {
                    tootBody += $"\n{metadata.MastodonSubmittedBy}";
                }
                else if (!string.IsNullOrWhiteSpace(metadata.TwitterSubmittedBy) && metadata.TwitterSubmittedBy.StartsWith("Submitted by"))
                {
                    tootBody += $"\nSubmitted by https://twitter.com/{metadata.TwitterUsername}";
                }
                else
                {
                    tootBody += $"\n{metadata.MastodonSubmittedBy}";
                }
            }
            else
            {
                tootBody += $"\nSubmission";
            }

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

            using MemoryStream mastodonAttachmentStream = new MemoryStream(photoBytes);
            MastodonAttachment? mastodonAttachment = await mastodonClient.UploadMedia(mastodonAttachmentStream);
            string mastodonAttachmentId = mastodonAttachment.Id;
            do
            {
                mastodonAttachment = await mastodonClient.GetAttachment(mastodonAttachmentId);
                await Task.Delay(500);
            }
            while (mastodonAttachment == null);

            MastodonStatus mastodonStatus = await mastodonClient.PublishStatus(tootBody, new List<string>() { mastodonAttachmentId }, null, "unlisted");

            ReportedItem newReportedItem = new ReportedItem()
            {
                TweetId = $"{Guid.NewGuid()}.0",
                CreatedAt = DateTime.UtcNow,
                NumberOfCars = metadata.NumberOfCars!.Value,
                Date = DateOnly.FromDateTime(metadata.PhotoDateTime),
                Time = TimeOnly.FromDateTime(metadata.PhotoDateTime),
                LocationString = metadata.PhotoCrossStreet,
                Location = new Microsoft.Azure.Cosmos.Spatial.Point(double.Parse(metadata.PhotoLongitude), double.Parse(metadata.PhotoLatitude)),
                TwitterLink = $"https://twitter.com/carbikelanesea/status/{tweet.ID}",
                MastodonLink = mastodonStatus.Url,
                Latest = true
            };
            newReportedItem.ImgurUrls.Add(imgurUpload.Link);
            bool addedToDatabase = await reportedItemsDatabase.AddReportedItem(newReportedItem);
            if (!addedToDatabase)
            {
                logger.LogError($"Failed to add tweet to database: {newReportedItem.TweetId}");
            }

            await feedProvider.AddReportedItemToFeed(newReportedItem);

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

        [HttpPost("/api/AdminPage/PostTweet")]
        public async Task<IActionResult> PostTweet([FromBody] PostTweetRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.PostUrl))
            {
                return BadRequest("Post URL is required.");
            }

            MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();

            if (request.PostUrl.Contains("twitter.com"))
            {
                Uri postUri = new Uri(request.PostUrl);
                if (ulong.TryParse(postUri.Segments.Last(), out ulong tweetIdNumber))
                {
                    TweetQuery? tweetQuery = await GetTweet(tweetIdNumber);
                    if (tweetQuery == null || tweetQuery.Tweets == null || tweetQuery.Tweets.Count == 0)
                    {
                        return NotFound($"Could not find tweet {request.PostUrl}");
                    }

                    Tweet tweet = tweetQuery.Tweets[0];
                    if (string.IsNullOrWhiteSpace(tweet.Text))
                    {
                        return BadRequest($"Tweet has no text. {request.PostUrl} with text {tweet.Text}");
                    }

                    string tweetText = helperMethods.FixTweetText(tweet.Text);
                    List<ReportedItem>? reportedItems = await helperMethods.TextToReportedItems(tweetText, mapsSearchClient);
                    if (reportedItems == null)
                    {
                        return BadRequest($"Couldn't find any reported items in tweet text. {request.PostUrl} with text {tweet.Text}");
                    }
                    
                    foreach (var reportedItem in reportedItems)
                    {
                        reportedItem.CreatedAt = tweet.CreatedAt!.Value;
                        reportedItem.TwitterLink = request.PostUrl;
                    }

                    // If attributed to a Twitter user convert the @ mention to a link instead so there's proper attribution on Mastodon
                    const string SubmittedBySearchText = "Submitted by @";
                    if (tweetText.Contains(SubmittedBySearchText))
                    {
                        int usernameStartIndex = tweetText.IndexOf(SubmittedBySearchText) + SubmittedBySearchText.Length - 1;
                        int potentialEndIndex = tweetText.IndexOf('\n', usernameStartIndex);
                        if (potentialEndIndex == -1)
                        {
                            potentialEndIndex = tweetText.IndexOf(' ', usernameStartIndex);
                        }
                        
                        if (potentialEndIndex == -1)
                        {
                            potentialEndIndex = tweetText.Length;
                        }

                        string username = tweetText[usernameStartIndex..potentialEndIndex];
                        string linkUsername = $"https://twitter.com/{username[1..]}";
                        tweetText = tweetText.Replace(username, linkUsername);
                    }

                    List<Stream> pictureStreams = new List<Stream>();

                    // If it's a regular tweet (ie not a quote tweet)
                    if (tweet.ReferencedTweets == null)
                    {
                        try
                        {
                            pictureStreams = await GetPhotosFromRegularTweet(tweet, tweetQuery);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, null);
                            return BadRequest(ex.Message);
                        }
                    }
                    // This gets images in quote tweets
                    else if (tweet.ReferencedTweets != null)
                    {
                        if (tweet.ReferencedTweets.Count == 1)
                        {
                            TweetReference tweetRef = tweet.ReferencedTweets[0];
                            if ("quoted" == tweetRef.Type)
                            {
                                TweetQuery? quotedTweet = await helperMethods.GetQuoteTweet(tweetRef.ID!, uploadTwitterContext);
                                if (quotedTweet != null)
                                {
                                    if (quotedTweet.Includes != null)
                                    {
                                        if (quotedTweet.Includes.Media != null)
                                        {
                                            foreach (var media in quotedTweet.Includes.Media)
                                            {
                                                if (media.Type == TweetMediaType.Photo)
                                                {
                                                    tweetQuery.Includes!.Media!.Add(media);
                                                }
                                            }
                                        }
                                        
                                        if (quotedTweet.Includes.Users != null && quotedTweet.Includes.Users.Count > 0)
                                        {
                                            // If this is a quote tweet try to include a link to the quoted tweet for proper attribution.
                                            tweetText += $"\nhttps://twitter.com/{quotedTweet.Includes.Users[0].Username}/status/{tweetRef.ID}";
                                        }
                                    }

                                    Tweet? includesQuotedTweet = tweetQuery.Includes!.Tweets!.FirstOrDefault(t => t.ID == tweetRef.ID);
                                    if (includesQuotedTweet != null)
                                    {
                                        if (includesQuotedTweet.Attachments != null && includesQuotedTweet.Attachments.MediaKeys != null &&
                                            includesQuotedTweet.Attachments.MediaKeys.Count > 0)
                                        {
                                            foreach (var mediaKey in includesQuotedTweet.Attachments.MediaKeys)
                                            {
                                                string? quotedTweetPictureUrl = helperMethods.GetUrlForMediaKey(mediaKey, tweetQuery.Includes.Media);
                                                if (quotedTweetPictureUrl != null)
                                                {
                                                    var stream = await helperMethods.DownloadImage(quotedTweetPictureUrl, httpClient);
                                                    if (stream == null)
                                                    {
                                                        helperMethods.DisposePictureStreams(pictureStreams);
                                                        return BadRequest($"Couldn't download picture with media key {mediaKey}. Id {tweet.ID} with text {tweet.Text}");
                                                    }
                                                    pictureStreams.Add(stream);
                                                }
                                            }
                                        }

                                        // In case the quoted tweet only has a video or gif instead of pictures.
                                        if (pictureStreams.Count == 0)
                                        {
                                            try
                                            {
                                                pictureStreams = await GetPhotosFromRegularTweet(tweet, tweetQuery);
                                            }
                                            catch (Exception ex)
                                            {
                                                logger.LogError(ex, null);
                                                return BadRequest(ex.Message);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (tweet.ReferencedTweets.Count > 1)
                        {
                            return BadRequest($"Number of referenced tweets is greater than 1. Id {tweet.ID} with text {tweet.Text}");
                        }
                    }

                    if (pictureStreams.Count == 0)
                    {
                        return BadRequest($"No picture streams for tweet. Id {tweet.ID} with text {tweet.Text}");
                    }

                    if (reportedItems.Count > 1 && pictureStreams.Count != reportedItems.Count)
                    {
                        helperMethods.DisposePictureStreams(pictureStreams);
                        return BadRequest($"Skipping transfer of tweet because the number of reported items is greater " +
                            $"than 1 but the number of pictures doesn't match the number of reported items. " +
                            $"Reported items: {reportedItems.Count}. Pictures: {pictureStreams.Count}." +
                            $"Id {tweet.ID} with text {tweet.Text}");
                    }

                    // First upload the pictures to imgur
                    await UploadImagesToImgur(reportedItems, pictureStreams);

                    List<string> attachmentIds = new List<string>();

                    // Next upload the images to Mastodon
                    foreach (var stream in pictureStreams)
                    {
                        try
                        {
                            MastodonAttachment? attachment = await mastodonClient.UploadMedia(stream);
                            attachmentIds.Add(attachment.Id);
                            string attachmentId = attachment.Id;
                            do
                            {
                                attachment = await mastodonClient.GetAttachment(attachmentId);
                                await Task.Delay(500);
                            }
                            while (attachment == null);
                        }
                        catch (Exception ex)
                        {
                            throw new ArgumentException($"Failed to upload image to Mastodon. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Id {tweet.ID} with text {tweet.Text}", ex);
                        }
                    }

                    // Finally post the status to Mastodon with the images
                    try
                    {
                        MastodonStatus status = await mastodonClient.PublishStatus(tweetText, attachmentIds, null, visibility: "unlisted");
                        helperMethods.DisposePictureStreams(pictureStreams);
                        foreach (var reportedItem in reportedItems)
                        {
                            reportedItem.MastodonLink = status.Url;
                            bool addedItem = await reportedItemsDatabase.AddReportedItem(reportedItem);
                            if (!addedItem)
                            {
                                logger.LogWarning($"Failed to update DB. DB ID {reportedItem.TweetId}. Imgur url: {string.Join(' ', reportedItem.ImgurUrls)}");
                            }

                            await feedProvider.AddReportedItemToFeed(reportedItem);
                        }
                    }
                    catch (Exception ex)
                    {
                        helperMethods.DisposePictureStreams(pictureStreams);
                        string error = $"Failed to publish status. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Attachment ids: {string.Join(' ', attachmentIds)} Id {tweet.ID} with text {tweet.Text}";
                        logger.LogError(ex, error);
                        return StatusCode((int)HttpStatusCode.InternalServerError, error);
                    }
                }
                else
                {
                    return BadRequest($"Could not find tweet id in link. {request.PostUrl}");
                }
            }
            else if (request.PostUrl.Contains("social.ridetrans.it"))
            {
                Uri postUri = new Uri(request.PostUrl);
                if (ulong.TryParse(postUri.Segments.Last(), out ulong tootIdNumber))
                {
                    MastodonStatus status = await mastodonClient.ViewPublicStatus(tootIdNumber.ToString());
                    string tootText = helperMethods.FixTootText(status.Content);
                    List<ReportedItem>? reportedItems = await helperMethods.TextToReportedItems(tootText, mapsSearchClient);
                    if (reportedItems == null)
                    {
                        return BadRequest($"Couldn't find any reported items in toot text. {request.PostUrl} with text {status.Content}");
                    }

                    foreach (var reportedItem in reportedItems)
                    {
                        reportedItem.CreatedAt = status.CreatedAt;
                        reportedItem.MastodonLink = request.PostUrl;
                    }

                    List<Stream> pictureStreams = new List<Stream>();

                    foreach (var attachment in status.MediaAttachments)
                    {
                        if (string.IsNullOrWhiteSpace(attachment.Url))
                        {
                            return BadRequest($"Toot picture doesn't have a URL. {request.PostUrl} with text {status.Content}");
                        }
                        var stream = await helperMethods.DownloadImage(attachment.Url, httpClient);
                        if (stream == null)
                        {
                            helperMethods.DisposePictureStreams(pictureStreams);
                            return BadRequest($"Couldn't download picture with media key {attachment.Id}. Id {status.Id} with text {status.Content}");
                        }
                        pictureStreams.Add(stream);
                    }

                    if (pictureStreams.Count == 0)
                    {
                        return BadRequest($"No picture streams for tweet. Id {status.Id} with text {status.Content}");
                    }

                    if (reportedItems.Count > 1 && pictureStreams.Count != reportedItems.Count)
                    {
                        helperMethods.DisposePictureStreams(pictureStreams);
                        return BadRequest($"Skipping transfer of tweet because the number of reported items is greater " +
                            $"than 1 but the number of pictures doesn't match the number of reported items. " +
                            $"Reported items: {reportedItems.Count}. Pictures: {pictureStreams.Count}." +
                            $"Id {status.Id} with text {status.Content}");
                    }

                    // First upload the pictures to imgur
                    await UploadImagesToImgur(reportedItems, pictureStreams);

                    List<string> mediaIds = new List<string>();

                    // Next upload the images to Twitter
                    foreach (var stream in pictureStreams)
                    {
                        MemoryStream ms = (MemoryStream)stream;
                        Media? twitterMedia = await uploadTwitterContext.UploadMediaAsync(ms.ToArray(), "image/jpg", "tweet_image");
                        if (twitterMedia == null)
                        {
                            helperMethods.DisposePictureStreams(pictureStreams);
                            string error = $"Failed to upload tweet image.";
                            logger.LogError(error);
                            return StatusCode((int)HttpStatusCode.InternalServerError, error);
                        }
                        mediaIds.Add(twitterMedia.MediaID.ToString());
                    }

                    // Finally post the tweet to Twitter with the images
                    Tweet? tweet = await uploadTwitterContext.TweetMediaAsync(tootText, mediaIds);
                    if (tweet == null)
                    {
                        helperMethods.DisposePictureStreams(pictureStreams);
                        string error = $"Failed to send tweet for Id {status.Id} with text {status.Content}.";
                        logger.LogError(error);
                        return StatusCode((int)HttpStatusCode.InternalServerError, error);
                    }

                    helperMethods.DisposePictureStreams(pictureStreams);

                    foreach (var reportedItem in reportedItems)
                    {
                        reportedItem.TwitterLink = $"https://twitter.com/carbikelanesea/status/{tweet.ID}";
                        bool addedItem = await reportedItemsDatabase.AddReportedItem(reportedItem);
                        if (!addedItem)
                        {
                            logger.LogWarning($"Failed to update DB. DB ID {reportedItem.TweetId}. Imgur url: {string.Join(' ', reportedItem.ImgurUrls)}");
                        }

                        await feedProvider.AddReportedItemToFeed(reportedItem);
                    }
                }
                else
                {
                    Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    throw new ArgumentException($"Could not find toot id in link. {request.PostUrl}");
                }
            }

            return NoContent();
        }

        [HttpDelete("/api/AdminPage/DeletePost")]
        public async Task<IActionResult> DeletePost([FromBody] DeletePostRequest request)
        {
            ReportedItem? reportedItem;
            try
            {
                reportedItem = await FindReportedItem(request.PostIdentifier);
                if (reportedItem == null)
                {
                    return NotFound($"No posts found with identifier {request.PostIdentifier}");
                }
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }

            if (reportedItem.ImgurUrls != null && reportedItem.ImgurUrls.Count > 0)
            {
                foreach (var imgurUrl in reportedItem.ImgurUrls)
                {
                    Uri imgurUri = new Uri(imgurUrl);
                    string imageHashUrl = imgurUri.Segments[imgurUri.Segments.Length - 1];
                    string[] splitImageHashUrl = imageHashUrl.Split('.');
                    string imgurId;
                    if (splitImageHashUrl.Length == 2 || splitImageHashUrl.Length == 1)
                    {
                        imgurId = splitImageHashUrl[0];
                    }
                    else
                    {
                        string errorString = $"Unexpected imgur hash url {imgurUri}. Split length: {splitImageHashUrl.Length}";
                        logger.LogError(errorString);
                        throw new Exception(errorString);
                    }

                    bool deletedImgurImage = await imgurImageEndpoint.DeleteImageAsync(imgurId);
                    if (!deletedImgurImage)
                    {
                        logger.LogWarning($"Could not delete Imgur image {imgurUrl} for {reportedItem.TweetId}");
                    }
                }
            }

            if (reportedItem.TwitterLink != null)
            {
                Uri twitterLink = new Uri(reportedItem.TwitterLink);
                string tweetId = twitterLink.Segments[twitterLink.Segments.Length - 1];
                var deleteTweetResponse = await uploadTwitterContext.DeleteTweetAsync(tweetId);
                if (deleteTweetResponse?.Data?.Deleted != true)
                {
                    logger.LogError($"Could not delete tweet: {reportedItem.TwitterLink}");
                }
            }

            if (reportedItem.MastodonLink != null)
            {
                Uri mastodonLink = new Uri(reportedItem.MastodonLink);
                string tootId = mastodonLink.Segments[mastodonLink.Segments.Length - 1];
                MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();
                await mastodonClient.DeleteStatus(tootId);
            }

            await feedProvider.RemoveReportedItemFromFeed(reportedItem);

            bool deletedFromDatabase = await reportedItemsDatabase.DeleteItem(reportedItem);
            if (!deletedFromDatabase)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, $"Failed to delete item from database. {reportedItem.TweetId}");
            }

            return NoContent();
        }

        [HttpPost("/api/AdminPage/PostMonthlyStats")]
        public async Task<IActionResult> PostMonthlyStats([FromBody] PostMonthlyStatsRequest request)
        {
            ReportedItem? mostRidiculousReportedItem;
            try
            {
                mostRidiculousReportedItem = await FindReportedItem(request.PostIdentifier);
                if (mostRidiculousReportedItem == null)
                {
                    return NotFound($"No posts found with identifier {request.PostIdentifier}");
                }
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }

            DateOnly lastMonth = DateOnly.FromDateTime(DateTime.Now).AddMonths(-1);
            DateOnly startOfLastMonth = new DateOnly(lastMonth.Year, lastMonth.Month, 1);
            DateOnly endOfLastMonth = new DateOnly(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month));

            if (mostRidiculousReportedItem.Date == null || mostRidiculousReportedItem.Date < startOfLastMonth || mostRidiculousReportedItem.Date > endOfLastMonth)
            {
                return BadRequest($"Most ridiculous item didn't occur last month. Item date: {mostRidiculousReportedItem.Date}");
            }

            List<ReportedItem> mostCars = await reportedItemsDatabase.GetMostCars(startOfLastMonth, endOfLastMonth);
            if (mostCars.Count > 1)
            {
                if (mostCars[0].NumberOfCars <= 2)
                {
                    return StatusCode((int)HttpStatusCode.InternalServerError, $"{mostCars.Count} reports with {mostCars[0].NumberOfCars} cars.");
                }
            }

            List<ReportedItem>? lastMonthItems = await reportedItemsDatabase.SearchItems(new Models.ReportedItemsSearchRequest()
            {
                MinDate = startOfLastMonth,
                MaxDate = endOfLastMonth
            });

            if (lastMonthItems == null || lastMonthItems.Count == 0)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, "No reports for last month.");
            }

            int largestReportCount = 0;
            List<ReportedItem> worstIntersectionItems = new List<ReportedItem>();
            for (int i = 0; i < lastMonthItems.Count; i++)
            {
                ReportedItem searchItem = lastMonthItems[i];
                if (searchItem.Location == null)
                {
                    continue;
                }

                int currentReportCount = 1;
                List<ReportedItem> currentIntersectionItems = new List<ReportedItem>();
                currentIntersectionItems.Add(searchItem);

                for (int j = i + 1; j < lastMonthItems.Count; j++)
                {
                    ReportedItem currentItem = lastMonthItems[j];
                    if (currentItem.Location == null)
                    {
                        continue;
                    }

                    // If the two locations are the same or are within 50 meters of each other (about half a block)
                    if (searchItem.Location == currentItem.Location || searchItem.Location.DistanceTo(currentItem.Location) <= 50)
                    {
                        currentReportCount += 1;
                        currentIntersectionItems.Add(currentItem);
                    }
                }

                if (currentReportCount > 1 && currentReportCount > largestReportCount)
                {
                    largestReportCount = currentReportCount;
                    worstIntersectionItems.Clear();
                    worstIntersectionItems.AddRange(currentIntersectionItems);
                }
            }

            if (worstIntersectionItems.Count == 0)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, "No worst intersection found.");
            }

            int worstIntersectionCarCount = worstIntersectionItems.Aggregate(0, (acc, cur) => cur.NumberOfCars + acc);

            int largestNameCount = 0;
            string worstIntersectionLocationString = string.Empty;
            for (int i = 0; i < worstIntersectionItems.Count; i++)
            {
                ReportedItem searchItem = worstIntersectionItems[i];
                int currentCount = 1;
                string currentLocationString = searchItem.LocationString;

                for (int j = i + 1; j < worstIntersectionItems.Count; j++)
                {
                    ReportedItem currentItem = worstIntersectionItems[j];
                    if (searchItem.LocationString == currentItem.LocationString)
                    {
                        currentCount += 1;
                    }
                }

                if (currentCount > largestNameCount)
                {
                    largestNameCount = currentCount;
                    worstIntersectionLocationString = currentLocationString;
                }
            }

            string introText = $"Stats for last month, the month of {lastMonth:MMMM}\n\n";
            string mostCarsText = $"Most cars reported at once: {mostCars[0].NumberOfCars} cars";
            string mostRidiculousText = $"Most ridiculous report: ";
            string worstIntersectionText = $"Worst intersection of the month: {worstIntersectionLocationString} with {worstIntersectionItems.Count} reports and {worstIntersectionCarCount} cars";

            // First post to Twitter
            try
            {
                Tweet? firstTweet = await uploadTwitterContext.TweetAsync($"{introText}{mostCarsText} {mostCars[0].TwitterLink}");
                Tweet? latestTweet = firstTweet;
                if (mostCars.Count > 1)
                {
                    for (int i = 1; i < mostCars.Count; i++)
                    {
                        var item = mostCars[i];
                        latestTweet = await uploadTwitterContext.ReplyAsync($"{mostCarsText} {item.TwitterLink}", latestTweet!.ID!);
                    }
                }
                Tweet? secondTweet = await uploadTwitterContext.ReplyAsync($"{mostRidiculousText} {mostRidiculousReportedItem.TwitterLink}", latestTweet!.ID!);
                Tweet? thirdTweet = await uploadTwitterContext.ReplyAsync($"{worstIntersectionText}", secondTweet!.ID!);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to tweet stats.");
            }

            // Next post to Mastodon
            MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();
            try
            {
                MastodonStatus firstToot = await mastodonClient.PublishStatus($"{introText}{mostCarsText} {mostCars[0].MastodonLink}");
                MastodonStatus latestToot = firstToot;
                if (mostCars.Count > 1)
                {
                    for (int i = 1; i < mostCars.Count; i++)
                    {
                        var item = mostCars[i];
                        latestToot = await mastodonClient.PublishStatus($"{mostCarsText} {item.MastodonLink}", inReplyToId: latestToot.Id);
                    }
                }
                MastodonStatus secondToot = await mastodonClient.PublishStatus($"{mostRidiculousText} {mostRidiculousReportedItem.MastodonLink}", inReplyToId: latestToot.Id);
                MastodonStatus thirdToot = await mastodonClient.PublishStatus($"{worstIntersectionText}", inReplyToId: secondToot.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to toot stats.");
            }

            return NoContent();
        }

        private async Task<ReportedItem?> FindReportedItem(string postIdentifier)
        {
            string? identifier = null;
            Uri uri;
            try
            {
                uri = new Uri(postIdentifier);
                identifier = uri.Segments[uri.Segments.Length - 1];
            }
            catch (UriFormatException)
            {
            }

            if (identifier == null)
            {
                identifier = postIdentifier;
            }

            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new Exception("Post identifier must either be a URL, a tweet/toot id, or a GUID");
            }

            List<ReportedItem>? reportedItems = await reportedItemsDatabase.GetItemUsingIdentifier(identifier);
            if (reportedItems == null || reportedItems.Count == 0)
            {
                return null;
            }

            return reportedItems[0];
        }

        private async Task<List<Stream>> GetPhotosFromRegularTweet(Tweet tweet, TweetQuery tweetQuery)
        {
            List<Stream> pictureStreams = new List<Stream>();
            if (tweet.Attachments == null || tweet.Attachments.MediaKeys == null || tweet.Attachments.MediaKeys.Count == 0)
            {
                throw new Exception($"Tweet does not contain any pictures. Id {tweet.ID} with text {tweet.Text}");
            }

            foreach (var mediaKey in tweet.Attachments.MediaKeys)
            {
                string? twitterPictureUrl = helperMethods.GetUrlForMediaKey(mediaKey, tweetQuery.Includes!.Media);
                if (twitterPictureUrl == null)
                {
                    throw new Exception($"Couldn't find media key {mediaKey}. Id {tweet.ID} with text {tweet.Text}");
                }

                var stream = await helperMethods.DownloadImage(twitterPictureUrl, httpClient);
                if (stream == null)
                {
                    helperMethods.DisposePictureStreams(pictureStreams);
                    throw new Exception($"Couldn't download picture with media key {mediaKey}. Id {tweet.ID} with text {tweet.Text}");
                }
                pictureStreams.Add(stream);
            }
            return pictureStreams;
        }

        private async Task UploadImagesToImgur(List<ReportedItem> reportedItems, List<Stream> pictureStreams)
        {
            int currentImageCount = 0;
            foreach (var stream in pictureStreams)
            {
                ReportedItem? reportedItem;

                if (reportedItems.Count == 1)
                {
                    reportedItem = reportedItems[0];
                }
                else
                {
                    reportedItem = reportedItems.Where(i => int.Parse(i.TweetId.Split('.')[1]) == currentImageCount).FirstOrDefault();
                    if (reportedItem == null)
                    {
                        helperMethods.DisposePictureStreams(pictureStreams);
                        Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        throw new ArgumentException($"Couldn't find reported item.");
                    }
                }

                // Create specific imgur stream because imgur upload disposes our stream
                MemoryStream imgurStream = new MemoryStream();
                await stream.CopyToAsync(imgurStream);
                imgurStream.Position = 0;
                stream.Position = 0;
                IImage imgurUpload;
                try
                {
                    // Note there is a bug in the ImgurAPI package where when a rate limit is hit an exception
                    // will be thrown because the package doesn't process rate limits correctly.
                    imgurUpload = await imgurImageEndpoint.UploadImageAsync(imgurStream);
                }
                catch (Exception ex)
                {
                    helperMethods.DisposePictureStreams(pictureStreams);
                    throw new ArgumentException($"Failed to upload image to imgur. Image count: {currentImageCount}.", ex);
                }

                // And save the imgur link to the DB
                reportedItem.ImgurUrls.Add(imgurUpload.Link);
                currentImageCount += 1;
            }
        }

        [HttpGet("/api/AdminPage/test")]
        public int Random()
        {
            Random random = new Random();
            return random.Next();
        }

        private async Task<TweetQuery?> GetTweet(ulong tweetIdNumber)
        {
            string tweetId = tweetIdNumber.ToString();
            TweetQuery tweetQuery = new TweetQuery()
            {
                Tweets = new List<Tweet>(),
                Includes = new TwitterInclude()
                {
                    Tweets = new List<Tweet>(),
                    Media = new List<TwitterMedia>(),
                    Users = new List<TwitterUser>()
                }
            };

            TweetQuery? tweetResponse = await (from tweet in uploadTwitterContext.Tweets
                                               where tweet.Type == TweetType.Lookup &&
                                               tweet.Ids == tweetId &&
                                               tweet.Expansions == $"{ExpansionField.MediaKeys},{ExpansionField.ReferencedTweetID}" &&
                                               tweet.MediaFields == MediaField.Url &&
                                               tweet.TweetFields == $"{TweetField.CreatedAt},{TweetField.ReferencedTweets}"
                                               select tweet).SingleOrDefaultAsync();
            if (tweetResponse != null)
            {
                if (tweetResponse.Tweets != null)
                {
                    tweetQuery.Tweets.AddRange(tweetResponse.Tweets);
                }

                if (tweetResponse.Includes != null)
                {
                    if (tweetResponse.Includes.Tweets != null)
                    {
                        tweetQuery.Includes.Tweets.AddRange(tweetResponse.Includes.Tweets);
                    }

                    if (tweetResponse.Includes.Media != null)
                    {
                        tweetQuery.Includes.Media.AddRange(tweetResponse.Includes.Media);
                    }

                    if (tweetResponse.Includes.Users != null)
                    {
                        tweetQuery.Includes.Users.AddRange(tweetResponse.Includes.Users);
                    }
                }
            }

            return tweetQuery;
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
                string twitterSubmittedBy = "Submission",
                string mastodonSubmittedBy = "Submission",
                bool? attribute = null,
                string? twitterUsername = null,
                string? twitterAccessToken = null,
                string? mastodonEndpoint = null,
                string? mastodonUsername = null,
                string? mastodonAccessToken = null) :
                base(numberOfCars,
                    photoId,
                    photoDateTime,
                    photoLatitude,
                    photoLongitude,
                    photoCrossStreet,
                    tags,
                    twitterSubmittedBy,
                    mastodonSubmittedBy,
                    attribute,
                    twitterUsername,
                    twitterAccessToken,
                    mastodonEndpoint,
                    mastodonUsername,
                    mastodonAccessToken)
            {
            }
        }

        public class PostTweetRequest
        {
            public string PostUrl { get; set; } = string.Empty;
        }

        public class DeletePostRequest
        {
            public string PostIdentifier { get; set; } = string.Empty;
        }

        public class PostMonthlyStatsRequest
        {
            public string PostIdentifier { get; set; } = string.Empty;
        }
    }
}
