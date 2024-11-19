using System.Net;
using Azure.Maps.Search;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Flurl;
using golf1052.atproto.net;
using golf1052.atproto.net.Models.AtProto.Repo;
using golf1052.atproto.net.Models.Bsky.Embed;
using golf1052.atproto.net.Models.Bsky.Feed;
using golf1052.atproto.net.Models.Bsky.Richtext;
using golf1052.Mastodon;
using golf1052.Mastodon.Models.Statuses;
using golf1052.Mastodon.Models.Statuses.Media;
using golf1052.ThreadsAPI;
using golf1052.ThreadsAPI.Models;
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
        private readonly IImageEndpoint imgurImageEndpoint;
        private readonly ReportedItemsDatabase reportedItemsDatabase;
        private readonly HttpClient httpClient;
        private readonly MapsSearchClient mapsSearchClient;
        private readonly MastodonClientProvider mastodonClientProvider;
        private readonly FeedProvider feedProvider;
        private readonly BlueskyClientProvider blueskyClientProvider;
        private readonly ThreadsClient threadsClient;

        public AdminPageController(ILogger<AdminPageController> logger,
            HelperMethods helperMethods,
            BlobContainerClient blobContainerClient,
            SecretClient secretClient,
            IImageEndpoint imgurImageEndpoint,
            ReportedItemsDatabase reportedItemsDatabase,
            HttpClient httpClient,
            MapsSearchClient mapsSearchClient,
            MastodonClientProvider mastodonClientProvider,
            FeedProvider feedProvider,
            BlueskyClientProvider blueskyClientProvider,
            ThreadsClient threadsClient)
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
            this.blueskyClientProvider = blueskyClientProvider;
            this.threadsClient = threadsClient;

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

        [HttpGet("/api/AdminPage/GetBlueskySession")]
        public async Task<BlueskySessionResponse> GetBlueskySession()
        {
            AtProtoClient blueskyClient = await blueskyClientProvider.GetClient();
            return new BlueskySessionResponse()
            {
                Did = blueskyClient.Did!,
                AccessJwt = blueskyClient.AccessJwt!
            };
        }

        [HttpGet("/api/AdminPage/PendingPhotos")]
        public async Task<Dictionary<string, List<FinalizedPhotoUploadWithSasUriMetadata>>> GetPendingPhotos()
        {
            Dictionary<string, List<FinalizedPhotoUploadWithSasUriMetadata>> submissions = new Dictionary<string, List<FinalizedPhotoUploadWithSasUriMetadata>>();
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
                if (metadata == null)
                {
                    logger.LogError($"Error when deserializing FinalizedPhotoUploadWithSasUriMetadata. Skipping.");
                    continue;
                }
                if (!submissions.ContainsKey(metadata.SubmissionId))
                {
                    submissions.Add(metadata.SubmissionId, new List<FinalizedPhotoUploadWithSasUriMetadata>());
                }
                BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata!.PhotoId}.jpeg");
                Uri photoUri = await photoBlobClient.GenerateUserDelegationReadOnlySasUri(DateTimeOffset.UtcNow.AddHours(1));
                metadata.Uri = photoUri.ToString();
                submissions[metadata.SubmissionId].Add(metadata);
            }

            foreach (var submission in submissions)
            {
                submission.Value.Sort((a, b) => a.PhotoNumber.CompareTo(b.PhotoNumber));
            }
            return submissions;
        }

        [HttpPost("/api/AdminPage/UploadTweet")]
        public async Task<IActionResult> UploadTweet([FromBody] List<FinalizedPhotoUploadMetadata> data)
        {
            string carsString;
            if (data.Count == 0)
            {
                string errorString = "Must pass at least 1 metadata object.";
                logger.LogError(errorString);
                return BadRequest(errorString);
            }
            FinalizedPhotoUploadMetadata metadata = data[0];

            MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();
            AtProtoClient blueskyClient;
            if (string.IsNullOrWhiteSpace(metadata.BlueskyAdminDid) || string.IsNullOrWhiteSpace(metadata.BlueskyAccessJwt))
            {
                blueskyClient = await blueskyClientProvider.GetClient();
            }
            else
            {
                blueskyClient = blueskyClientProvider.GetClient(metadata.BlueskyAdminDid, metadata.BlueskyAccessJwt);
            }

            if (metadata.NumberOfCars == 1)
            {
                carsString = "car";
            }
            else
            {
                carsString = "cars";
            }

            string postBody = $"{metadata.NumberOfCars} {carsString}\n" +
                $"Date: {metadata.PhotoDateTime!.Value.ToString("M/d/yyyy")}\n" +
                $"Time: {metadata.PhotoDateTime!.Value.ToString("h:mm tt")}\n" +
                $"Location: {metadata.PhotoCrossStreet}\n" +
                $"GPS: {metadata.PhotoLatitude}, {metadata.PhotoLongitude}";

            string tootBody = postBody;
            if (!string.IsNullOrWhiteSpace(metadata.MastodonSubmittedBy))
            {
                if (metadata.MastodonSubmittedBy.StartsWith("Submitted by"))
                {
                    tootBody += $"\n{metadata.MastodonSubmittedBy}";
                }
                else if (!string.IsNullOrWhiteSpace(metadata.TwitterSubmittedBy) &&
                    metadata.TwitterSubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.TwitterUsername))
                {
                    tootBody += $"\nSubmitted by {GetTwitterLinkFromTwitterUsername(metadata.TwitterUsername)}";
                }
                else if (!string.IsNullOrWhiteSpace(metadata.BlueskySubmittedBy) &&
                    metadata.BlueskySubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.BlueskyHandle))
                {
                    tootBody += $"\nSubmitted by {GetBlueskyLinkFromBlueskyHandle(metadata.BlueskyHandle)}";
                }
                else if (!string.IsNullOrWhiteSpace(metadata.ThreadsSubmittedBy) &&
                    metadata.ThreadsSubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.ThreadsUsername))
                {
                    tootBody += $"\nSubmitted by {GetThreadsLinkFromThreadsUsername(metadata.ThreadsUsername)}";
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

            string skeetBody = postBody;
            List<BskyFacet> facets = new List<BskyFacet>();
            if (!string.IsNullOrWhiteSpace(metadata.BlueskySubmittedBy))
            {
                if (metadata.BlueskySubmittedBy.StartsWith("Submitted by"))
                {
                    string blueskyHandle = $"@{metadata.BlueskyHandle}";
                    skeetBody += $"\nSubmitted by {blueskyHandle}";
                    int handleStartIndex = skeetBody.IndexOf(blueskyHandle);

                    facets.Add(new BskyFacet()
                    {
                        Index = new BskyByteSlice()
                        {
                            ByteStart = handleStartIndex,
                            ByteEnd = handleStartIndex + blueskyHandle.Length
                        },
                        Features = new List<BskyFeature>()
                        {
                            new BskyMention()
                            {
                                Did = metadata.BlueskyUserDid!
                            }
                        }
                    });
                }
                else if (!string.IsNullOrWhiteSpace(metadata.TwitterSubmittedBy) &&
                    metadata.TwitterSubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.TwitterUsername))
                {
                    string twitterLink = GetTwitterLinkFromTwitterUsername(metadata.TwitterUsername);
                    skeetBody += $"\nSubmitted by {twitterLink}";
                    int linkStartIndex = skeetBody.IndexOf(twitterLink);

                    facets.Add(new BskyFacet()
                    {
                        Index = new BskyByteSlice()
                        {
                            ByteStart = linkStartIndex,
                            ByteEnd = linkStartIndex + twitterLink.Length
                        },
                        Features = new List<BskyFeature>()
                        {
                            new BskyLink()
                            {
                                Uri = twitterLink
                            }
                        }
                    });
                }
                else if (!string.IsNullOrWhiteSpace(metadata.MastodonSubmittedBy) &&
                    metadata.MastodonSubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.MastodonEndpoint) &&
                    !string.IsNullOrWhiteSpace(metadata.MastodonUsername))
                {
                    string mastodonLink = GetMastodonLinkFromMastodonHandle(metadata.MastodonEndpoint, metadata.MastodonUsername);
                    skeetBody += $"\nSubmitted by {mastodonLink}";
                    int linkStartIndex = skeetBody.IndexOf(mastodonLink);

                    facets.Add(new BskyFacet()
                    {
                        Index = new BskyByteSlice()
                        {
                            ByteStart = linkStartIndex,
                            ByteEnd = linkStartIndex + mastodonLink.Length
                        },
                        Features = new List<BskyFeature>()
                        {
                            new BskyLink()
                            {
                                Uri = mastodonLink
                            }
                        }
                    });
                }
                else if (!string.IsNullOrWhiteSpace(metadata.ThreadsSubmittedBy) &&
                    metadata.ThreadsSubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.ThreadsUsername))
                {
                    string threadsLink = GetThreadsLinkFromThreadsUsername(metadata.ThreadsUsername);
                    skeetBody += $"\nSubmitted by {threadsLink}";
                    int linkStartIndex = skeetBody.IndexOf(threadsLink);

                    facets.Add(new BskyFacet()
                    {
                        Index = new BskyByteSlice()
                        {
                            ByteStart = linkStartIndex,
                            ByteEnd = linkStartIndex + threadsLink.Length
                        },
                        Features = new List<BskyFeature>()
                        {
                            new BskyLink()
                            {
                                Uri = threadsLink
                            }
                        }
                    });
                }
                else
                {
                    skeetBody += $"\n{metadata.BlueskySubmittedBy}";
                }
            }
            else
            {
                skeetBody += $"\nSubmission";
            }

            string threadsBody = postBody;
            if (!string.IsNullOrWhiteSpace(metadata.ThreadsSubmittedBy))
            {
                if (metadata.ThreadsSubmittedBy.StartsWith("Submitted by"))
                {
                    threadsBody += $"\n{metadata.ThreadsSubmittedBy}";
                }
                else if (!string.IsNullOrWhiteSpace(metadata.TwitterSubmittedBy) &&
                    metadata.TwitterSubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.TwitterUsername))
                {
                    threadsBody += $"\nSubmitted by {GetTwitterLinkFromTwitterUsername(metadata.TwitterUsername)}";
                }
                else if (!string.IsNullOrWhiteSpace(metadata.MastodonSubmittedBy) &&
                    metadata.MastodonSubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.MastodonEndpoint) &&
                    !string.IsNullOrWhiteSpace(metadata.MastodonUsername))
                {
                    threadsBody += $"\nSubmitted by {GetMastodonLinkFromMastodonHandle(metadata.MastodonEndpoint, metadata.MastodonUsername)}";
                }
                else if (!string.IsNullOrWhiteSpace(metadata.BlueskySubmittedBy) &&
                    metadata.BlueskySubmittedBy.StartsWith("Submitted by") &&
                    !string.IsNullOrWhiteSpace(metadata.BlueskyHandle))
                {
                    threadsBody += $"\nSubmitted by {GetBlueskyLinkFromBlueskyHandle(metadata.BlueskyHandle)}";
                }
                else
                {
                    threadsBody += $"\n{metadata.ThreadsSubmittedBy}";
                }
            }

            List<BlobClient> photoBlobClients = new List<BlobClient>();
            List<IImage> imgurUploads = new List<IImage>();
            List<Stream> pictureStreams = new List<Stream>();

            foreach (var d in data)
            {
                BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{d.PhotoId}.jpeg");
                photoBlobClients.Add(photoBlobClient);
                var photoDownload = await photoBlobClient.DownloadContentAsync();
                var photoBytes = photoDownload.Value.Content.ToArray();
                pictureStreams.Add(new MemoryStream(photoBytes));
            }

            ReportedItem newReportedItem = new ReportedItem()
            {
                TweetId = $"{Guid.NewGuid()}.0",
                CreatedAt = DateTime.UtcNow,
                NumberOfCars = metadata.NumberOfCars!.Value,
                Date = DateOnly.FromDateTime(metadata.PhotoDateTime.Value),
                Time = TimeOnly.FromDateTime(metadata.PhotoDateTime.Value),
                LocationString = metadata.PhotoCrossStreet!,
                Location = new Microsoft.Azure.Cosmos.Spatial.Point(double.Parse(metadata.PhotoLongitude!), double.Parse(metadata.PhotoLatitude!)),
                TwitterLink = metadata.TwitterLink,
                Latest = true
            };

            List<ReportedItem> reportedItems = new List<ReportedItem>() { newReportedItem };

            List<string> imgurLinks = await UploadImagesToImgur(reportedItems, pictureStreams);
            newReportedItem.ImgurUrls.AddRange(imgurUploads.Select(i => i.Link));

            Task mastodonUploadTask = UploadPostToMastodon(mastodonClient, reportedItems, pictureStreams, tootBody, postBody);
            Task blueskyUploadTask = UploadPostToBluesky(blueskyClient, reportedItems, pictureStreams, skeetBody, facets, postBody);
            Task threadsUploadTask = UploadPostToThreads(threadsClient, reportedItems, imgurLinks, threadsBody);
            await Task.WhenAll(mastodonUploadTask, blueskyUploadTask, threadsUploadTask);
            await blueskyUploadTask;

            bool addedToDatabase = await reportedItemsDatabase.AddReportedItem(newReportedItem);
            if (!addedToDatabase)
            {
                logger.LogError($"Failed to add tweet to database: {newReportedItem.TweetId}");
            }

            await feedProvider.AddReportedItemToFeed(newReportedItem);

            foreach (var d in data)
            {
                BlobClient metadataBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{d.PhotoId}.json");
                await metadataBlobClient.DeleteAsync();
            }

            foreach (var photoBlobClient in photoBlobClients)
            {
                await photoBlobClient.DeleteAsync();
            }

            helperMethods.DisposePictureStreams(pictureStreams);

            return NoContent();
        }

        [HttpDelete("/api/AdminPage/DeletePendingPhoto")]
        public async Task DeletePendingPhoto([FromBody] List<FinalizedPhotoUploadMetadata> data)
        {
            if (data.Count == 0)
            {
                throw new Exception("Must pass at least 1 metadata object.");
            }

            foreach (var metadata in data)
            {
                BlobClient photoBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata.PhotoId}.jpeg");
                BlobClient metadataBlobClient = blobContainerClient.GetBlobClient($"{UploadController.FinalizedUploadPrefix}{metadata.PhotoId}.json");
                await photoBlobClient.DeleteAsync();
                await metadataBlobClient.DeleteAsync();
            }
            Response.StatusCode = (int)HttpStatusCode.NoContent;
        }

        [HttpPost("/api/AdminPage/PostTweet")]
        public async Task<IActionResult> PostTweet([FromBody] PostTweetRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.PostUrl) &&
                string.IsNullOrWhiteSpace(request.TweetBody) && string.IsNullOrWhiteSpace(request.TweetImages) && string.IsNullOrWhiteSpace(request.TweetLink))
            {
                return BadRequest("Post URL or tweet body, tweet images, and tweet link is required.");
            }

            MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();
            AtProtoClient blueskyClient;
            if (string.IsNullOrWhiteSpace(request.BlueskyDid) || string.IsNullOrWhiteSpace(request.BlueskyAccessJwt))
            {
                blueskyClient = await blueskyClientProvider.GetClient();
            }
            else
            {
                blueskyClient = blueskyClientProvider.GetClient(request.BlueskyDid, request.BlueskyAccessJwt);
            }

            if (!string.IsNullOrWhiteSpace(request.TweetBody) && !string.IsNullOrWhiteSpace(request.TweetImages) && !string.IsNullOrWhiteSpace(request.TweetLink))
            {
                string tweetText = request.TweetBody;
                List<ReportedItem>? reportedItems = await helperMethods.TextToReportedItems(tweetText, mapsSearchClient);
                if (reportedItems == null)
                {
                    return BadRequest($"Couldn't find any reported items in tweet text.");
                }

                foreach (var reportedItem in reportedItems)
                {
                    reportedItem.CreatedAt = DateTime.UtcNow; // It's not actually now but we can't get the real time from the tweet.
                    reportedItem.TwitterLink = request.TweetLink;
                }

                List<BskyFacet> facets = new List<BskyFacet>();

                // If attributed to a Twitter user convert the @ mention to a link instead so there's proper attribution on Mastodon
                const string SubmittedBySearchText = "Submitted by ";
                string mastodonText = tweetText;
                if (tweetText.Contains(SubmittedBySearchText))
                {
                    int usernameStartIndex = tweetText.IndexOf(SubmittedBySearchText) + SubmittedBySearchText.Length;
                    int potentialEndIndex = tweetText.IndexOf('\n', usernameStartIndex);
                    if (potentialEndIndex == -1)
                    {
                        potentialEndIndex = tweetText.IndexOf(' ', usernameStartIndex);
                    }

                    if (potentialEndIndex == -1)
                    {
                        potentialEndIndex = tweetText.Length;
                    }

                    if (tweetText[usernameStartIndex] == '@')
                    {
                        string username = tweetText[usernameStartIndex..potentialEndIndex];
                        string linkUsername = $"https://twitter.com/{username[1..]}";
                        tweetText = tweetText.Replace(username, linkUsername);
                        mastodonText = mastodonText.Replace(username, linkUsername);

                        facets.Add(new BskyFacet()
                        {
                            Index = new BskyByteSlice()
                            {
                                ByteStart = usernameStartIndex,
                                ByteEnd = usernameStartIndex + linkUsername.Length
                            },
                            Features = new List<BskyFeature>()
                            {
                                new BskyLink()
                                {
                                    Uri = linkUsername
                                }
                            }
                        });
                    }
                    else if (tweetText[usernameStartIndex] == 'h')
                    {
                        string profileLink = tweetText[usernameStartIndex..potentialEndIndex];
                        Uri profileUri = new Uri(profileLink);
                        if (!profileUri.Host.Contains("bsky.app"))
                        {
                            mastodonText = mastodonText.Replace(profileLink, $"{profileUri.Segments[^1]}@{profileUri.Host}");
                        }

                        facets.Add(new BskyFacet()
                        {
                            Index = new BskyByteSlice()
                            {
                                ByteStart = usernameStartIndex,
                                ByteEnd = usernameStartIndex + profileLink.Length
                            },
                            Features = new List<BskyFeature>()
                            {
                                new BskyLink()
                                {
                                    Uri = profileLink
                                }
                            }
                        });
                    }
                }
                else if (tweetText.Contains("https://"))
                {
                    int linkStartIndex = tweetText.IndexOf("https://");
                    int potentialEndIndex = tweetText.IndexOf('\n', linkStartIndex);
                    if (potentialEndIndex == -1)
                    {
                        potentialEndIndex = tweetText.IndexOf(' ', linkStartIndex);
                    }

                    if (potentialEndIndex == -1)
                    {
                        potentialEndIndex = tweetText.Length;
                    }
                    string link = tweetText[linkStartIndex..potentialEndIndex];

                    facets.Add(new BskyFacet()
                    {
                        Index = new BskyByteSlice()
                        {
                            ByteStart = linkStartIndex,
                            ByteEnd = linkStartIndex + link.Length
                        },
                        Features = new List<BskyFeature>()
                        {
                            new BskyLink()
                            {
                                Uri = link
                            }
                        }
                    });
                }

                if (!string.IsNullOrWhiteSpace(request.QuoteTweetLink))
                {
                    facets.Add(new BskyFacet()
                    {
                        Index = new BskyByteSlice()
                        {
                            ByteStart = tweetText.Length + 1,
                            ByteEnd = tweetText.Length + 1 + request.QuoteTweetLink.Length
                        },
                        Features = new List<BskyFeature>()
                        {
                            new BskyLink()
                            {
                                Uri = request.QuoteTweetLink
                            }
                        }
                    });
                    tweetText += $"\n{request.QuoteTweetLink}";
                }

                List<string> tweetImageLinks = new List<string>();
                List<Stream> pictureStreams = new List<Stream>();
                if (!string.IsNullOrWhiteSpace(request.TweetImages))
                {
                    string[] splitTweetImages = request.TweetImages.Split('\n');
                    foreach (string imageLink in splitTweetImages)
                    {
                        tweetImageLinks.Add(imageLink);
                        Url imageLinkUrl = new Url(imageLink);
                        if (imageLinkUrl.Host.Contains("twimg") && imageLinkUrl.QueryParams.Contains("name"))
                        {
                            // Set image quality to medium because Bluesky has a small max image size currently
                            imageLinkUrl.SetQueryParam("name", "medium");
                        }
                        Stream? pictureStream = await helperMethods.DownloadImage(imageLinkUrl.ToString(), httpClient);
                        if (pictureStream == null)
                        {
                            return BadRequest($"Couldn't get stream from image link {imageLinkUrl}");
                        }

                        pictureStreams.Add(pictureStream);
                    }
                }

                if (pictureStreams.Count == 0)
                {
                    return BadRequest($"No picture streams for tweet.");
                }

                // First upload the pictures to imgur
                await UploadImagesToImgur(reportedItems, pictureStreams);

                // Then upload to the three platforms
                Task mastodonUploadTask = UploadPostToMastodon(mastodonClient, reportedItems, pictureStreams, mastodonText, request.TweetBody);
                Task blueskyUploadTask = UploadPostToBluesky(blueskyClient, reportedItems, pictureStreams, tweetText, facets, tweetText);
                Task threadsUploadTask = UploadPostToThreads(threadsClient, reportedItems, tweetImageLinks, tweetText);
                await Task.WhenAll(mastodonUploadTask, blueskyUploadTask, threadsUploadTask);

                foreach (var reportedItem in reportedItems)
                {
                    bool addedItem = await reportedItemsDatabase.AddReportedItem(reportedItem);
                    if (!addedItem)
                    {
                        logger.LogWarning($"Failed to update DB. DB ID {reportedItem.TweetId}. Imgur url: {string.Join(' ', reportedItem.ImgurUrls)}");
                    }

                    await feedProvider.AddReportedItemToFeed(reportedItem);
                }

                helperMethods.DisposePictureStreams(pictureStreams);
            }
            else if (request.PostUrl.Contains("twitter.com") || request.PostUrl.Contains("x.com"))
            {
                throw new Exception("Reading tweets from Twitter no longer works. Please copy/paste the tweet body, image links, and tweet link into the appropriate locations.");
            }

            return NoContent();
        }

        private async Task UploadPostToMastodon(MastodonClient mastodonClient,
            List<ReportedItem> reportedItems,
            List<Stream> pictureStreams,
            string mastodonText,
            string originalPostBody)
        {
            List<string> attachmentIds = new List<string>();

            // Upload the images to Mastodon
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
                    logger.LogError(ex, $"Failed to upload image to Mastodon. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Text {originalPostBody}");
                    return;
                }
            }

            // Next post the status to Mastodon with the images
            try
            {
                MastodonStatus status = await mastodonClient.PublishStatus(mastodonText, attachmentIds, null, visibility: "unlisted");
                foreach (var reportedItem in reportedItems)
                {
                    reportedItem.MastodonLink = status.Url;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to publish Mastodon status. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Attachment ids: {string.Join(' ', attachmentIds)} Text {originalPostBody}");
                return;
            }
        }

        private async Task UploadPostToBluesky(AtProtoClient blueskyClient,
            List<ReportedItem> reportedItems,
            List<Stream> pictureStreams,
            string skeetBody,
            List<BskyFacet> facets,
            string originalPostBody)
        {
            List<AtProtoBlob> blobs = new List<AtProtoBlob>();
            foreach (var stream in pictureStreams)
            {
                try
                {
                    UploadBlobRequest uploadBlobRequest = new UploadBlobRequest()
                    {
                        Content = stream,
                        MimeType = "image/jpeg"
                    };
                    UploadBlobResponse uploadBlobResponse = await blueskyClient.UploadBlob(uploadBlobRequest);
                    blobs.Add(uploadBlobResponse.Blob);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to upload image to Bluesky. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Text {originalPostBody}");
                    return;
                }
            }

            try
            {
                BskyPost blueskyPost = new BskyPost<BskyImages>()
                {
                    Text = skeetBody,
                    CreatedAt = DateTime.UtcNow,
                    Embed = new BskyImages()
                    {
                        Images = blobs.Select(blob => new BskyImage()
                        {
                            Image = blob,
                            Alt = string.Empty
                        }).ToList()
                    }
                };

                if (facets.Count > 0)
                {
                    blueskyPost.Facets = facets;
                }

                CreateRecordRequest<BskyPost> createRecordRequest = new CreateRecordRequest<BskyPost>()
                {
                    Repo = blueskyClient.Did!,
                    Collection = BskyPost.Type,
                    Record = blueskyPost
                };
                CreateRecordResponse createRecordResponse = await blueskyClient.CreateRecord(createRecordRequest);

                foreach (var reportedItem in reportedItems)
                {
                    reportedItem.BlueskyLink = helperMethods.GetBlueskyPostUrl(createRecordResponse.Uri);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to publish Bluesky status. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Blob ids: {string.Join(' ', blobs.Select(b => b.Ref.Link))} Text {originalPostBody}");
                return;
            }
        }

        private async Task UploadPostToThreads(ThreadsClient threadsClient,
            List<ReportedItem> reportedItems,
            List<string> tweetImageLinks,
            string threadsBody)
        {
            if (tweetImageLinks.Count == 1)
            {
                string threadsMediaContainerId = await threadsClient.CreateThreadsMediaContainer("IMAGE",
                    threadsBody,
                    tweetImageLinks[0]);
                // Threads API recommends waiting 30 seconds between creating the media container and publishing it
                // but we'll check the container status API instead.
                var containerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, threadsMediaContainerId);
                if (containerStatus.Status == "FINISHED")
                {
                    string threadsPostId = await threadsClient.PublishThreadsMediaContainer(threadsMediaContainerId);
                    ThreadsMediaObject uploadedThreadPost = await threadsClient.GetThreadsMediaObject(threadsPostId,
                        "id,permalink");

                    foreach (var reportedItem in reportedItems)
                    {
                        reportedItem.ThreadsLink = uploadedThreadPost.Permalink;
                    }
                }
            }
            else if (tweetImageLinks.Count > 1)
            {
                List<string> containerIds = new List<string>(tweetImageLinks.Count);
                foreach (var imageLink in tweetImageLinks)
                {
                    string threadsMediaContainerId = await threadsClient.CreateThreadsMediaContainer("IMAGE",
                        null,
                        imageLink,
                        null,
                        null,
                        true);
                    containerIds.Add(threadsMediaContainerId);
                }
                string carouselContainerId = await threadsClient.CreateThreadsMediaContainer("CAROUSEL",
                    threadsBody,
                    null,
                    null,
                    null,
                    null,
                    containerIds);
                var containerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, carouselContainerId);
                if (containerStatus.Status == "FINISHED")
                {
                    string threadsPostId = await threadsClient.PublishThreadsMediaContainer(carouselContainerId);
                    ThreadsMediaObject uploadedThreadsPost = await threadsClient.GetThreadsMediaObject(threadsPostId,
                        "id,permalink");

                    foreach (var reportedItem in reportedItems)
                    {
                        reportedItem.ThreadsLink = uploadedThreadsPost.Permalink;
                    }
                }
            }
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

            if (reportedItem.MastodonLink != null)
            {
                Uri mastodonLink = new Uri(reportedItem.MastodonLink);
                string tootId = mastodonLink.Segments[mastodonLink.Segments.Length - 1];
                MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();
                await mastodonClient.DeleteStatus(tootId);
            }

            if (reportedItem.BlueskyLink != null)
            {
                Uri blueskyLink = new Uri(reportedItem.BlueskyLink);
                AtProtoClient blueskyClient = await blueskyClientProvider.GetClient();
                DeleteRecordRequest deleteRecordRequest = new DeleteRecordRequest()
                {
                    Repo = blueskyClient.Did!,
                    Collection = "app.bsky.feed.post",
                    Rkey = blueskyLink.Segments.Last()
                };
                await blueskyClient.DeleteRecord(deleteRecordRequest);
            }

            // TODO: Add Threads deletion support once Threads API supports deletion

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

            bool skipMostCars = false;
            List<ReportedItem> mostCars = await reportedItemsDatabase.GetMostCars(startOfLastMonth, endOfLastMonth);
            if (mostCars.Count > 1)
            {
                if (mostCars[0].NumberOfCars <= 2)
                {
                    skipMostCars = true;
                    logger.LogWarning($"Skipping most cars report. {mostCars.Count} reports with {mostCars[0].NumberOfCars} cars.");
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

            // TODO: Instead of posting the following to Twitter, return the content that needs to be manually posted.
            // First post to Twitter
            try
            {
                //Tweet? firstTweet = await uploadTwitterContext.TweetAsync($"{introText}{mostCarsText} {mostCars[0].TwitterLink}");
                if (!skipMostCars)
                {
                    Console.WriteLine($"T1: {introText}{mostCarsText} {mostCars[0].TwitterLink}");
                }
                else
                {
                    Console.WriteLine($"T1: {introText}");
                }
                //Tweet? latestTweet = firstTweet;
                if (!skipMostCars)
                {
                    if (mostCars.Count > 1)
                    {
                        for (int i = 1; i < mostCars.Count; i++)
                        {
                            var item = mostCars[i];
                            //latestTweet = await uploadTwitterContext.ReplyAsync($"{mostCarsText} {item.TwitterLink}", latestTweet!.ID!);
                            Console.WriteLine($"TM{i + 1}: {mostCarsText} {item.TwitterLink}");
                        }
                    }
                }
                //Tweet? secondTweet = await uploadTwitterContext.ReplyAsync($"{mostRidiculousText} {mostRidiculousReportedItem.TwitterLink}", latestTweet!.ID!);
                Console.WriteLine($"T2: {mostRidiculousText} {mostRidiculousReportedItem.TwitterLink}");
                //Tweet? thirdTweet = await uploadTwitterContext.ReplyAsync($"{worstIntersectionText}", secondTweet!.ID!);
                Console.WriteLine($"T3: {worstIntersectionText}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to tweet stats.");
            }

            // Next post to Mastodon
            MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();
            try
            {
                MastodonStatus firstToot;
                if (!skipMostCars)
                {
                    firstToot = await mastodonClient.PublishStatus($"{introText}{mostCarsText} {mostCars[0].MastodonLink}");
                }
                else
                {
                    firstToot = await mastodonClient.PublishStatus($"{introText}");
                }

                MastodonStatus latestToot = firstToot;
                if (!skipMostCars)
                {
                    if (mostCars.Count > 1)
                    {
                        for (int i = 1; i < mostCars.Count; i++)
                        {
                            var item = mostCars[i];
                            latestToot = await mastodonClient.PublishStatus($"{mostCarsText} {item.MastodonLink}", inReplyToId: latestToot.Id);
                        }
                    }
                }
                MastodonStatus secondToot = await mastodonClient.PublishStatus($"{mostRidiculousText} {mostRidiculousReportedItem.MastodonLink}", inReplyToId: latestToot.Id);
                MastodonStatus thirdToot = await mastodonClient.PublishStatus($"{worstIntersectionText}", inReplyToId: secondToot.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to toot stats.");
            }

            // Next post to Bluesky
            AtProtoClient blueskyClient = await blueskyClientProvider.GetClient();
            try
            {
                CreateRecordResponse firstSkeet;
                if (!skipMostCars)
                {
                    string firstSkeetText = $"{introText}{mostCarsText} ";
                    string firstSkeetLink = GetSocialLinkForBluesky(mostCars[0])!;
                    BskyFacet firstSkeetFacet = new BskyFacet
                    {
                        Index = new BskyByteSlice()
                        {
                            ByteStart = firstSkeetText.Length,
                            ByteEnd = firstSkeetText.Length + firstSkeetLink.Length
                        },
                        Features = new List<BskyFeature>()
                    {
                        new BskyLink()
                        {
                            Uri = firstSkeetLink
                        }
                    }
                    };
                    firstSkeetText += firstSkeetLink;
                    firstSkeet = await blueskyClient.CreateRecord(new CreateRecordRequest<BskyPost>()
                    {
                        Repo = blueskyClient.Did!,
                        Collection = BskyPost.Type,
                        Record = new BskyPost()
                        {
                            Text = firstSkeetText,
                            CreatedAt = DateTime.UtcNow,
                            Facets = new List<BskyFacet>()
                        {
                            firstSkeetFacet
                        }
                        }
                    });
                }
                else
                {
                    firstSkeet = await blueskyClient.CreateRecord(new CreateRecordRequest<BskyPost>()
                    {
                        Repo = blueskyClient.Did!,
                        Collection = BskyPost.Type,
                        Record = new BskyPost()
                        {
                            Text = introText,
                            CreatedAt = DateTime.UtcNow
                        }
                    });
                }

                CreateRecordResponse latestSkeet = firstSkeet;
                if (mostCars.Count > 1)
                {
                    for (int i = 1; i < mostCars.Count; i++)
                    {
                        var item = mostCars[i];
                        string latestSkeetText = $"{mostCarsText} ";
                        string latestSkeetLink = GetSocialLinkForBluesky(item)!;
                        BskyFacet latestSkeetFacet = new BskyFacet
                        {
                            Index = new BskyByteSlice()
                            {
                                ByteStart = latestSkeetText.Length,
                                ByteEnd = latestSkeetText.Length + latestSkeetLink.Length
                            },
                            Features = new List<BskyFeature>()
                            {
                                new BskyLink()
                                {
                                    Uri = latestSkeetLink
                                }
                            }
                        };
                        latestSkeetText += latestSkeetLink;
                        latestSkeet = await blueskyClient.CreateRecord(new CreateRecordRequest<BskyPost>()
                        {
                            Repo = blueskyClient.Did!,
                            Collection = BskyPost.Type,
                            Record = new BskyPost()
                            {
                                Text = latestSkeetText,
                                CreatedAt = DateTime.UtcNow,
                                Facets = new List<BskyFacet>()
                                {
                                    latestSkeetFacet
                                },
                                Reply = new BskyPostReplyRef()
                                {
                                    Root = new AtProtoStrongRef()
                                    {
                                        Cid = latestSkeet.Cid,
                                        Uri = latestSkeet.Uri
                                    },
                                    Parent = new AtProtoStrongRef()
                                    {
                                        Cid = latestSkeet.Cid,
                                        Uri = latestSkeet.Uri
                                    }
                                }
                            }
                        });
                    }
                }
                string secondSkeetText = $"{mostRidiculousText} ";
                string secondSkeetLink = GetSocialLinkForBluesky(mostRidiculousReportedItem)!;
                BskyFacet secondSkeetFacet = new BskyFacet
                {
                    Index = new BskyByteSlice()
                    {
                        ByteStart = secondSkeetText.Length,
                        ByteEnd = secondSkeetText.Length + secondSkeetLink.Length
                    },
                    Features = new List<BskyFeature>()
                    {
                        new BskyLink()
                        {
                            Uri = secondSkeetLink
                        }
                    }
                };
                secondSkeetText += secondSkeetLink;
                CreateRecordResponse secondSkeet = await blueskyClient.CreateRecord(new CreateRecordRequest<BskyPost>()
                {
                    Repo = blueskyClient.Did!,
                    Collection = BskyPost.Type,
                    Record = new BskyPost()
                    {
                        Text = secondSkeetText,
                        CreatedAt = DateTime.UtcNow,
                        Facets = new List<BskyFacet>()
                        {
                            secondSkeetFacet
                        },
                        Reply = new BskyPostReplyRef()
                        {
                            Root = new AtProtoStrongRef()
                            {
                                Cid = latestSkeet.Cid,
                                Uri = latestSkeet.Uri
                            },
                            Parent = new AtProtoStrongRef()
                            {
                                Cid = latestSkeet.Cid,
                                Uri = latestSkeet.Uri
                            }
                        }
                    }
                });
                string thirdSkeetText = $"{worstIntersectionText}";
                CreateRecordResponse thirdSkeet = await blueskyClient.CreateRecord(new CreateRecordRequest<BskyPost>()
                {
                    Repo = blueskyClient.Did!,
                    Collection = BskyPost.Type,
                    Record = new BskyPost()
                    {
                        Text = thirdSkeetText,
                        CreatedAt = DateTime.UtcNow,
                        Reply = new BskyPostReplyRef()
                        {
                            Root = new AtProtoStrongRef()
                            {
                                Cid = secondSkeet.Cid,
                                Uri = secondSkeet.Uri
                            },
                            Parent = new AtProtoStrongRef()
                            {
                                Cid = secondSkeet.Cid,
                                Uri = secondSkeet.Uri
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to skeet status.");
            }

            // Finally post to Threads
            try
            {
                string firstThreadsPostId = string.Empty;
                if (!skipMostCars)
                {
                    string firstCreationId = await threadsClient.CreateThreadsMediaContainer("TEXT",
                        $"{introText}{mostCarsText} {GetSocialLinkForThreads(mostCars[0])}");
                    var firstContainerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, firstCreationId);
                    if (firstContainerStatus.Status == "FINISHED")
                    {
                        firstThreadsPostId = await threadsClient.PublishThreadsMediaContainer(firstCreationId);
                    }
                }
                else
                {
                    string firstCreationId = await threadsClient.CreateThreadsMediaContainer("TEXT",
                        $"{introText}");
                    var firstContainerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, firstCreationId);
                    if (firstContainerStatus.Status == "FINISHED")
                    {
                        firstThreadsPostId = await threadsClient.PublishThreadsMediaContainer(firstCreationId);
                    }
                }

                string latestThreadsPostId = firstThreadsPostId;
                if (!skipMostCars)
                {
                    if (mostCars.Count > 1)
                    {
                        for (int i = 1; i < mostCars.Count; i++)
                        {
                            var item = mostCars[i];
                            string creationId = await threadsClient.CreateThreadsMediaContainer("TEXT",
                                $"{mostCarsText} {GetSocialLinkForThreads(item)}",
                                replyToId: latestThreadsPostId);
                            var containerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, creationId);
                            if (containerStatus.Status == "FINISHED")
                            {
                                latestThreadsPostId = await threadsClient.PublishThreadsMediaContainer(creationId);
                            }
                        }
                    }
                }

                string secondCreationId = await threadsClient.CreateThreadsMediaContainer("TEXT",
                    $"{mostRidiculousText} {GetSocialLinkForThreads(mostRidiculousReportedItem)}",
                    replyToId: latestThreadsPostId);
                var secondContainerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, secondCreationId);
                string secondThreadsPostId = string.Empty;
                if (secondContainerStatus.Status == "FINISHED")
                {
                    secondThreadsPostId = await threadsClient.PublishThreadsMediaContainer(secondCreationId);
                }

                string thirdCreationId = await threadsClient.CreateThreadsMediaContainer("TEXT",
                    $"{worstIntersectionText}",
                    replyToId: secondThreadsPostId);
                var thirdContainerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, thirdCreationId);
                if (thirdContainerStatus.Status == "FINISHED")
                {
                    string thirdThreadsPostId = await threadsClient.PublishThreadsMediaContainer(thirdCreationId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post stats to Threads.");
            }

            return NoContent();
        }

        private string? GetSocialLinkForBluesky(ReportedItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.BlueskyLink))
            {
                return item.BlueskyLink;
            }
            else if (!string.IsNullOrWhiteSpace(item.MastodonLink))
            {
                return item.MastodonLink;
            }
            else
            {
                return item.TwitterLink;
            }
        }

        private string? GetSocialLinkForThreads(ReportedItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.ThreadsLink))
            {
                return item.ThreadsLink;
            }
            else if (!string.IsNullOrWhiteSpace(item.MastodonLink))
            {
                return item.MastodonLink;
            }
            else
            {
                return item.TwitterLink;
            }
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

        private async Task<List<string>> UploadImagesToImgur(List<ReportedItem> reportedItems, List<Stream> pictureStreams)
        {
            List<string> imgurLinks = new List<string>();
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
                imgurLinks.Add(imgurUpload.Link);
                currentImageCount += 1;
            }
            return imgurLinks;
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

        public string GetTwitterLinkFromTwitterUsername(string twitterUsername)
        {
            return $"https://twitter.com/{twitterUsername}";
        }

        public string GetMastodonLinkFromMastodonHandle(string mastodonEndpoint, string mastodonUsername)
        {
            Uri mastodonEndpointUri = new Uri(mastodonEndpoint);
            return $"https://{mastodonEndpointUri.Host}/@{mastodonUsername}";
        }

        public string GetBlueskyLinkFromBlueskyHandle(string blueskyHandle)
        {
            return $"https://bsky.app/profile/{blueskyHandle}";
        }

        public string GetThreadsLinkFromThreadsUsername(string threadsUsername)
        {
            return $"https://threads.net/@{threadsUsername}";
        }

        public class FinalizedPhotoUploadWithSasUriMetadata : FinalizedPhotoUploadMetadata
        {
            public string? Uri { get; set; }

            // Here because of bug when trying to deserialize values types to nullable value type properties
            // See https://github.com/dotnet/runtime/issues/44428
            public FinalizedPhotoUploadWithSasUriMetadata()
            {
            }

            public FinalizedPhotoUploadWithSasUriMetadata(int numberOfCars,
                string photoId,
                string submissionId,
                int photoNumber,
                DateTime photoDateTime,
                string photoLatitude,
                string photoLongitude,
                string photoCrossStreet,
                List<ImageTag> tags,
                bool userSpecifiedDateTime,
                bool userSpecifiedLocation,
                string twitterSubmittedBy = "Submission",
                string mastodonSubmittedBy = "Submission",
                string blueskySubmittedBy = "Submission",
                string threadsSubmittedBy = "Submission",
                bool? attribute = null,
                string? twitterUsername = null,
                string? twitterAccessToken = null,
                string? mastodonEndpoint = null,
                string? mastodonUsername = null,
                string? mastodonAccessToken = null,
                string? blueskyHandle = null,
                string? threadsUsername = null,
                string? threadsAccessToken = null) :
                base(numberOfCars,
                    photoId,
                    submissionId,
                    photoNumber,
                    photoDateTime,
                    photoLatitude,
                    photoLongitude,
                    photoCrossStreet,
                    tags,
                    userSpecifiedDateTime,
                    userSpecifiedLocation,
                    twitterSubmittedBy,
                    mastodonSubmittedBy,
                    blueskySubmittedBy,
                    threadsSubmittedBy,
                    attribute,
                    twitterUsername,
                    twitterAccessToken,
                    mastodonEndpoint,
                    mastodonUsername,
                    mastodonAccessToken,
                    blueskyHandle,
                    threadsUsername,
                    threadsAccessToken)
            {
            }
        }

        public class PostTweetRequest
        {
            public string PostUrl { get; set; } = string.Empty;
            public string TweetBody { get; set; } = string.Empty;
            public string TweetImages { get; set; } = string.Empty;
            public string TweetLink { get; set; } = string.Empty;
            public string QuoteTweetLink { get; set; } = string.Empty;
            public string BlueskyDid { get; set; } = string.Empty;
            public string BlueskyAccessJwt { get; set; } = string.Empty;
        }

        public class DeletePostRequest
        {
            public string PostIdentifier { get; set; } = string.Empty;
        }

        public class PostMonthlyStatsRequest
        {
            public string PostIdentifier { get; set; } = string.Empty;
        }

        public record BlueskySessionResponse
        {
            public required string Did { get; init; }
            public required string AccessJwt { get; init; }
        }
    }
}
