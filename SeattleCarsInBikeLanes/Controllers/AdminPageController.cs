using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Maps.Search;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using FishyFlip;
using FishyFlip.Lexicon.App.Bsky.Embed;
using FishyFlip.Lexicon.App.Bsky.Feed;
using FishyFlip.Models;
using FishyFlip.Tools;
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
using Newtonsoft.Json;
using SeattleCarsInBikeLanes.Core.Contracts;
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
        private readonly BlueskyOAuthProvider blueskyOAuthProvider;
        private readonly ThreadsClient threadsClient;
        private readonly ReportStore reportStore;
        private const string FinalizedUploadPrefix = ReportStore.FinalizedUploadPrefix;
        private const string PartialPublicationWarning = "Some posts may already have been published. Check Bluesky, Mastodon and Threads, delete any successful posts, fix the cause shown in the logs, then click Upload again.";

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
            BlueskyOAuthProvider blueskyOAuthProvider,
            ThreadsClient threadsClient,
            ReportStore reportStore)
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
            this.blueskyOAuthProvider = blueskyOAuthProvider;
            this.threadsClient = threadsClient;
            this.reportStore = reportStore;

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
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<Dictionary<string, List<FinalizedPhotoUploadWithSasUriMetadata>>> GetPendingPhotos(CancellationToken ct = default)
        {
            Dictionary<string, List<FinalizedPhotoUploadWithSasUriMetadata>> submissions = new Dictionary<string, List<FinalizedPhotoUploadWithSasUriMetadata>>();
            await foreach (var report in reportStore.GetPendingAsync(ct))
            {
                List<FinalizedPhotoUploadWithSasUriMetadata> photos = new List<FinalizedPhotoUploadWithSasUriMetadata>();
                foreach (var photo in report.Photos)
                {
                    var preview = ToPreview(photo.Metadata);
                    preview.ReportId = report.Receipt.ReportId;
                    preview.ReportVersion = report.Version;
                    preview.DeviceId = report.DeviceId;
                    preview.ModerationStatus = report.Moderation?.Kind ?? "pending";
                    preview.ModerationOperationId = report.Moderation?.Id;
                    preview.ModerationStartedAt = report.Moderation?.StartedAt;
                    preview.Warning = report.Moderation is null ? null :
                        report.Moderation.Kind == "deleting"
                            ? "Deletion is in progress. Wait for the request to finish, then refresh."
                            : "Publication is in progress. Wait for the request to finish before retrying.";
                    preview.CanModerate = report.Moderation is null;
                    await SetPreviewAsync(preview, photo, ct);
                    photos.Add(preview);
                }
                if (photos.Any(p => !p.CanModerate))
                {
                    photos.ForEach(p => p.CanModerate = false);
                }
                submissions.Add(report.Receipt.ReportId, photos);
            }

            foreach (var group in await ReadLegacyGroupsAsync(ct))
            {
                // Consult all states, including tombstones left behind after partial cleanup.
                if (await reportStore.ExistsAsync(group.ReportId, ct))
                {
                    continue;
                }
                List<FinalizedPhotoUploadWithSasUriMetadata> photos = new List<FinalizedPhotoUploadWithSasUriMetadata>();
                foreach (var metadata in group.Metadata.OrderBy(p => p.PhotoNumber))
                {
                    var preview = ToPreview(metadata);
                    preview.ReportId = group.ReportId;
                    preview.ReportVersion = group.Version;
                    preview.LegacySubmissionId = group.SubmissionId;
                    preview.Warning = group.Error;
                    preview.CanModerate = group.Error is null;
                    preview.ModerationStatus = group.Error is null ? "pending" : "needs-repair";
                    var photo = group.Photos.FirstOrDefault(p => p.Metadata.PhotoId == metadata.PhotoId);
                    if (photo is not null)
                    {
                        await SetPreviewAsync(preview, photo, ct);
                    }
                    photos.Add(preview);
                }
                if (photos.Any(p => !p.CanModerate))
                {
                    photos.ForEach(p => p.CanModerate = false);
                }
                submissions.TryAdd(group.ReportId, photos);
            }
            return submissions;
        }

        [HttpPost("/api/AdminPage/UploadTweet")]
        public async Task<IActionResult> UploadTweet([FromBody] ModerateReportRequest request, CancellationToken ct = default)
        {
            SubmissionReport report;
            try
            {
                report = await AcquireModerationAsync(request, "publishing", ct);
            }
            catch (Exception ex)
            {
                return ModerationError(ex);
            }

            bool effectsStarted = false;
            string publicationStage = "preparation";
            List<Stream> pictureStreams = new List<Stream>();
            try
            {
                await PublishReportAsync(report, request, pictureStreams, stage =>
                                                {
                                                    effectsStarted = true;
                                                    publicationStage = stage;
                                                }, ct);
                publicationStage = "report retirement";
                await reportStore.RetireAsync(report.Receipt.ReportId, report.Moderation!.Id, ct);
                await TryCleanupAsync();
                return NoContent();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Publication failed during {Stage} for report {ReportId}, operation {OperationId}, external work started: {EffectsStarted}",
                                                    publicationStage, report.Receipt.ReportId, report.Moderation!.Id, effectsStarted);
                try
                {
                    if (publicationStage == "report retirement")
                    {
                        var saved = await reportStore.GetAsync(report.Receipt.ReportId, report.DeviceId, CancellationToken.None);
                        if (saved?.Retired == true && saved.Moderation?.Id == report.Moderation.Id)
                        {
                            return NoContent();
                        }
                    }
                    // We check/delete partial social posts before retrying. A failed request
                    // must leave the report pending with its photos.
                    await reportStore.ReleaseModerationAsync(report.Receipt.ReportId, report.Moderation.Id, CancellationToken.None);
                    var pending = await reportStore.GetForModerationAsync(report.Receipt.ReportId, CancellationToken.None);
                    bool invalidPhoto = !effectsStarted &&
                        (ex is InvalidDataException || ex is RequestFailedException { Status: 404 or 412 });
                    var failure = new
                    {
                        message = effectsStarted
                            ? $"Upload failed during {publicationStage}. The report and its photos are still pending. {PartialPublicationWarning}"
                            : invalidPhoto
                                ? "A saved photo is missing, changed or invalid. Nothing was posted. Repair the stored photo before retrying."
                                : "Publication failed before external posting. The report is still pending; fix the cause shown in the logs, then click Upload again.",
                        reportId = report.Receipt.ReportId,
                        reportVersion = pending.Version,
                        retryAllowed = pending.Moderation is null
                    };
                    return invalidPhoto ? Conflict(failure) : StatusCode(effectsStarted ? 502 : 503, failure);
                }
                catch (Exception releaseError)
                {
                    logger.LogError(releaseError, "Could not confirm pending state for failed report {ReportId}", report.Receipt.ReportId);
                    return StatusCode(503, new
                    {
                        message = $"Upload failed during {publicationStage}, and the saved report state could not be refreshed. Fix the storage error shown in the logs and refresh before retrying.",
                        reportId = report.Receipt.ReportId,
                        operationId = report.Moderation.Id
                    });
                }
            }
            finally
            {
                foreach (var stream in pictureStreams)
                {
                    stream.Dispose();
                }
            }
        }

        private async Task PublishReportAsync(SubmissionReport report, ModerateReportRequest request,
            List<Stream> pictureStreams, Action<string> externalEffectsStarting, CancellationToken ct)
        {
            var data = report.Photos.Select(p => p.Metadata).ToList();
            FinalizedPhotoUploadMetadata metadata = data[0];
            string carsString;
            MastodonClient mastodonClient = mastodonClientProvider.GetServerClient();
            AtProtoClient blueskyClient;
            if (string.IsNullOrWhiteSpace(request.BlueskyAdminDid) || string.IsNullOrWhiteSpace(request.BlueskyAccessJwt))
            {
                blueskyClient = await blueskyClientProvider.GetClient();
            }
            else
            {
                blueskyClient = blueskyClientProvider.GetClient(request.BlueskyAdminDid, request.BlueskyAccessJwt);
            }

            if (metadata.NumberOfCars == 1)
            {
                carsString = "car";
            }
            else
            {
                carsString = "cars";
            }

            // Handles can be reassigned between a report being submitted and being published, and
            // that gap is often days. The DID is permanent, so re-resolve the current handle from
            // it rather than attributing the post to a handle that may now belong to someone else.
            if (!string.IsNullOrWhiteSpace(metadata.BlueskyUserDid))
            {
                foreach (var photo in data)
                {
                    if (!string.IsNullOrWhiteSpace(photo.BlueskyHandle) &&
                                                                photo.BlueskySubmittedBy == $"Submitted by {photo.BlueskyHandle}")
                    {
                        photo.BlueskySubmittedBy = $"Submitted by @{photo.BlueskyHandle}";
                    }
                }
                string? currentHandle = await blueskyOAuthProvider.ResolveHandleFromDid(metadata.BlueskyUserDid);
                if (!string.IsNullOrWhiteSpace(currentHandle) && currentHandle != metadata.BlueskyHandle)
                {
                    logger.LogInformation("Bluesky handle for {Did} changed from {OldHandle} to {NewHandle} since submission.",
                                                                metadata.BlueskyUserDid, metadata.BlueskyHandle, currentHandle);

                    foreach (var d in data)
                    {
                        if (d.BlueskySubmittedBy == $"Submitted by @{d.BlueskyHandle}")
                        {
                            d.BlueskySubmittedBy = $"Submitted by @{currentHandle}";
                        }
                        d.BlueskyHandle = currentHandle;
                    }
                }
            }

            string postBody = $"{metadata.NumberOfCars} {carsString}\n" +
                $"Date: {metadata.PhotoDateTime!.Value.ToString("M/d/yyyy")}\n" +
                $"Time: {metadata.PhotoDateTime!.Value.ToString("h:mm tt")}\n" +
                $"Location: {metadata.PhotoCrossStreet}\n" +
                $"GPS: {metadata.PhotoLatitude}, {metadata.PhotoLongitude}";

            string tootBody = postBody;
            if (!string.IsNullOrWhiteSpace(metadata.MastodonSubmittedBy))
            {
                if (metadata.MastodonSubmittedBy != "Submission")
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
                if (!string.IsNullOrWhiteSpace(metadata.BlueskyHandle) && !string.IsNullOrWhiteSpace(metadata.BlueskyUserDid) &&
                                                    metadata.BlueskySubmittedBy == $"Submitted by @{metadata.BlueskyHandle}")
                {
                    string blueskyHandle = $"@{metadata.BlueskyHandle}";
                    skeetBody += $"\nSubmitted by {blueskyHandle}";
                    int handleStartIndex = skeetBody.IndexOf(blueskyHandle);

                    facets.Add(new BskyFacet()
                    {
                        Index = new BskyByteSlice()
                        {
                            ByteStart = Encoding.UTF8.GetByteCount(skeetBody[..handleStartIndex]),
                            ByteEnd = Encoding.UTF8.GetByteCount(skeetBody[..(handleStartIndex + blueskyHandle.Length)])
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
                else if (metadata.BlueskySubmittedBy != "Submission")
                {
                    skeetBody += $"\n{metadata.BlueskySubmittedBy}";
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
                            ByteStart = Encoding.UTF8.GetByteCount(skeetBody[..linkStartIndex]),
                            ByteEnd = Encoding.UTF8.GetByteCount(skeetBody[..(linkStartIndex + twitterLink.Length)])
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
                            ByteStart = Encoding.UTF8.GetByteCount(skeetBody[..linkStartIndex]),
                            ByteEnd = Encoding.UTF8.GetByteCount(skeetBody[..(linkStartIndex + mastodonLink.Length)])
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
                            ByteStart = Encoding.UTF8.GetByteCount(skeetBody[..linkStartIndex]),
                            ByteEnd = Encoding.UTF8.GetByteCount(skeetBody[..(linkStartIndex + threadsLink.Length)])
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
                if (metadata.ThreadsSubmittedBy != "Submission")
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

            List<byte[]> photoBuffers = new List<byte[]>();
            foreach (var photo in report.Photos)
            {
                ValidatePhotoReference(photo);
                BlobClient photoBlobClient = blobContainerClient.GetBlobClient(photo.BlobName);
                var photoDownload = await photoBlobClient.DownloadContentAsync(new BlobDownloadOptions()
                {
                    Conditions = new BlobRequestConditions() { IfMatch = new ETag(photo.ETag) }
                }, ct);
                var photoBytes = photoDownload.Value.Content.ToArray();
                if (photoBytes.LongLength != photo.Length)
                {
                    throw new InvalidDataException("A stored photo has changed. Repair the report before publishing.");
                }
                photoBuffers.Add(photoBytes);
                pictureStreams.Add(new MemoryStream(photoBytes, writable: false));
            }

            ReportedItem newReportedItem = new ReportedItem()
            {
                TweetId = $"{report.Receipt.ReportId}.0",
                CreatedAt = DateTime.UtcNow,
                NumberOfCars = metadata.NumberOfCars!.Value,
                Date = DateOnly.FromDateTime(metadata.PhotoDateTime.Value),
                Time = TimeOnly.FromDateTime(metadata.PhotoDateTime.Value),
                LocationString = metadata.PhotoCrossStreet!,
                Location = new Microsoft.Azure.Cosmos.Spatial.Point(double.Parse(metadata.PhotoLongitude!, CultureInfo.InvariantCulture), double.Parse(metadata.PhotoLatitude!, CultureInfo.InvariantCulture)),
                TwitterLink = metadata.TwitterLink,
                Latest = true
            };

            List<ReportedItem> reportedItems = new List<ReportedItem>() { newReportedItem };

            List<string> imgurLinks = await UploadImagesToImgur(reportedItems, pictureStreams,
                () => externalEffectsStarting("Imgur upload"));

            // Independent cursors/lifetimes share the same immutable photo bytes.
            List<Stream> mastodonStreams = new List<Stream>();
            List<Stream> blueskyStreams = new List<Stream>();
            foreach (byte[] bytes in photoBuffers)
            {
                MemoryStream mastodonStream = new MemoryStream(bytes, writable: false);
                MemoryStream blueskyStream = new MemoryStream(bytes, writable: false);
                mastodonStreams.Add(mastodonStream);
                blueskyStreams.Add(blueskyStream);
                pictureStreams.Add(mastodonStream);
                pictureStreams.Add(blueskyStream);
            }
            externalEffectsStarting("social publication");
            await Task.WhenAll(
                UploadPostToMastodon(mastodonClient, reportedItems, mastodonStreams, tootBody, postBody, failOnError: true),
                UploadPostToBluesky(blueskyClient, reportedItems, blueskyStreams, skeetBody, facets, postBody, failOnError: true),
                UploadPostToThreads(threadsClient, reportedItems, imgurLinks, threadsBody, failOnError: true));
            if (string.IsNullOrWhiteSpace(newReportedItem.MastodonLink) ||
                string.IsNullOrWhiteSpace(newReportedItem.BlueskyLink) ||
                string.IsNullOrWhiteSpace(newReportedItem.ThreadsLink))
            {
                throw new InvalidOperationException("One or more social publications were not confirmed.");
            }
            externalEffectsStarting("database persistence");
            await reportedItemsDatabase.SavePublishedReportAsync(newReportedItem, ct);

            externalEffectsStarting("feed persistence");
            await feedProvider.AddReportedItemToFeed(newReportedItem);

        }

        [HttpDelete("/api/AdminPage/DeletePendingPhoto")]
        public async Task<IActionResult> DeletePendingPhoto([FromBody] ModerateReportRequest request, CancellationToken ct = default)
        {
            SubmissionReport report;
            try
            {
                report = await AcquireModerationAsync(request, "deleting", ct);
            }
            catch (Exception ex)
            {
                return ModerationError(ex);
            }
            try
            {
                await reportStore.RetireAsync(report.Receipt.ReportId, report.Moderation!.Id, ct);
                await TryCleanupAsync();
                return NoContent();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deletion could not be confirmed for {ReportId}, operation {OperationId}",
                                                    report.Receipt.ReportId, report.Moderation!.Id);
                return StatusCode(503, new
                {
                    message = "Deletion could not be confirmed. Fix the storage error shown in the logs, then refresh to check the saved report state.",
                    reportId = report.Receipt.ReportId,
                    operationId = report.Moderation.Id
                });
            }
        }

        private async Task<SubmissionReport> AcquireModerationAsync(ModerateReportRequest request, string kind, CancellationToken ct)
        {
            if (!ReportStore.IsValidReportId(request.ReportId) || string.IsNullOrWhiteSpace(request.ReportVersion))
            {
                throw new ArgumentException("A report ID and version are required. Refresh pending reports.");
            }
            if (request.PhotoIds is null || request.PhotoIds.Count == 0)
            {
                throw new ArgumentException("The complete ordered photo set is required.");
            }
            if (kind == "publishing" && request.Edits is null)
            {
                throw new ArgumentException("Publication corrections are required.");
            }
            if (kind == "publishing")
            {
                ValidateEdits(request.Edits!);
            }
            SubmissionReport report;
            string? expectedVersion = request.ReportVersion;
            if (request.ReportVersion.StartsWith("legacy:", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(request.LegacySubmissionId) ||
                                                    ReportStore.LegacyReportId(request.LegacySubmissionId) != request.ReportId)
                {
                    throw new ArgumentException("The saved submission does not match this report.");
                }
                if (await reportStore.ExistsAsync(request.ReportId, ct))
                {
                    throw new ModerationConflictException();
                }
                var group = (await ReadLegacyGroupsAsync(ct)).SingleOrDefault(g => g.ReportId == request.ReportId);
                if (group is null || group.Version != request.ReportVersion)
                {
                    throw new ModerationConflictException();
                }
                if (group.Error is not null)
                {
                    throw new InvalidDataException(group.Error);
                }
                RequireWholePhotoSet(request.PhotoIds, group.Photos);
                report = await reportStore.AdoptLegacyAsync(group.SubmissionId, group.Photos, ct);
                expectedVersion = report.Version;
            }
            else
            {
                report = await reportStore.GetForModerationAsync(request.ReportId, ct);
            }

            RequireWholePhotoSet(request.PhotoIds, report.Photos);
            var overrides = kind == "publishing"
                ? report.Photos.Select(p => ApplyEdits(p.Metadata, request.Edits!)).ToList()
                : null;
            return await reportStore.BeginModerationAsync(request.ReportId, kind, overrides, expectedVersion, ct);
        }

        private static void RequireWholePhotoSet(IReadOnlyList<string> ids, IReadOnlyList<SubmissionPhoto> photos)
        {
            if (!ids.SequenceEqual(photos.Select(p => p.Metadata.PhotoId), StringComparer.Ordinal))
            {
                throw new ArgumentException("The complete ordered photo set must match the saved report. Refresh pending reports.");
            }
        }

        private static void ValidateEdits(AdminPublicationEdits edits)
        {
            UploadLimits limits = new UploadLimits();
            if (edits.NumberOfCars < 1 || edits.PhotoDateTime == default ||
                string.IsNullOrWhiteSpace(edits.PhotoCrossStreet) || edits.PhotoCrossStreet.Length > 500 ||
                !double.TryParse(edits.PhotoLatitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(edits.PhotoLongitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
                latitude < limits.SouthLatitude || latitude > limits.NorthLatitude ||
                longitude < limits.WestLongitude || longitude > limits.EastLongitude)
            {
                throw new ArgumentException("Enter a positive car count, date/time, location and valid Seattle GPS coordinates.");
            }
            if (new[] { edits.TwitterSubmittedBy, edits.MastodonSubmittedBy, edits.BlueskySubmittedBy, edits.ThreadsSubmittedBy }
                            .Any(value => value?.Length > 1000))
            {
                throw new ArgumentException("Attribution text must be at most 1,000 characters.");
            }
            if (!string.IsNullOrWhiteSpace(edits.TwitterLink) &&
                            (edits.TwitterLink.Length > 2048 || !System.Uri.TryCreate(edits.TwitterLink, UriKind.Absolute, out var link) ||
                             (link.Scheme != "https" && link.Scheme != "http")))
            {
                throw new ArgumentException("Twitter link must be an HTTP or HTTPS URL.");
            }
        }

        private static FinalizedPhotoUploadMetadata ApplyEdits(FinalizedPhotoUploadMetadata source, AdminPublicationEdits edits)
        {
            var metadata = SanitizedCopy(source);
            metadata.NumberOfCars = edits.NumberOfCars;
            metadata.PhotoDateTime = edits.PhotoDateTime;
            metadata.PhotoCrossStreet = edits.PhotoCrossStreet.Trim();
            metadata.PhotoLatitude = edits.PhotoLatitude.Trim();
            metadata.PhotoLongitude = edits.PhotoLongitude.Trim();
            metadata.TwitterSubmittedBy = edits.TwitterSubmittedBy?.Trim() ?? "Submission";
            metadata.MastodonSubmittedBy = edits.MastodonSubmittedBy?.Trim() ?? "Submission";
            metadata.BlueskySubmittedBy = edits.BlueskySubmittedBy?.Trim() ?? "Submission";
            metadata.ThreadsSubmittedBy = edits.ThreadsSubmittedBy?.Trim() ?? "Submission";
            metadata.TwitterLink = edits.TwitterLink?.Trim();
            return metadata;
        }

        private static FinalizedPhotoUploadMetadata SanitizedCopy(FinalizedPhotoUploadMetadata source)
        {
            return new FinalizedPhotoUploadMetadata()
            {
                PhotoId = source.PhotoId,
                SubmissionId = source.SubmissionId,
                PhotoNumber = source.PhotoNumber,
                ReportId = source.ReportId,
                DeviceId = source.DeviceId,
                PhotoDateTime = source.PhotoDateTime,
                PhotoLatitude = source.PhotoLatitude,
                PhotoLongitude = source.PhotoLongitude,
                PhotoCrossStreet = source.PhotoCrossStreet,
                Tags = source.Tags?.Where(t => t is not null).Select(t => new ImageTag() { Name = t.Name, Confidence = t.Confidence }).ToList() ?? [],
                NumberOfCars = source.NumberOfCars,
                UserSpecifiedDateTime = source.UserSpecifiedDateTime,
                UserSpecifiedLocation = source.UserSpecifiedLocation,
                Attribute = source.Attribute,
                TwitterSubmittedBy = source.TwitterSubmittedBy,
                MastodonSubmittedBy = source.MastodonSubmittedBy,
                BlueskySubmittedBy = source.BlueskySubmittedBy,
                ThreadsSubmittedBy = source.ThreadsSubmittedBy,
                TwitterUsername = source.TwitterUsername,
                TwitterLink = source.TwitterLink,
                MastodonEndpoint = source.MastodonEndpoint,
                MastodonUsername = source.MastodonUsername,
                MastodonFullUsername = source.MastodonFullUsername,
                BlueskyHandle = source.BlueskyHandle,
                BlueskyUserDid = source.BlueskyUserDid,
                ThreadsUsername = source.ThreadsUsername
            };
        }

        private static FinalizedPhotoUploadWithSasUriMetadata ToPreview(FinalizedPhotoUploadMetadata metadata)
        {
            return JsonConvert.DeserializeObject<FinalizedPhotoUploadWithSasUriMetadata>(JsonConvert.SerializeObject(SanitizedCopy(metadata)))!;
        }

        private async Task SetPreviewAsync(FinalizedPhotoUploadWithSasUriMetadata preview, SubmissionPhoto photo, CancellationToken ct)
        {
            try
            {
                ValidatePhotoReference(photo);
                var blob = blobContainerClient.GetBlobClient(photo.BlobName);
                var properties = await blob.GetPropertiesAsync(new BlobRequestConditions() { IfMatch = new ETag(photo.ETag) }, ct);
                if (properties.Value.ContentLength != photo.Length)
                {
                    throw new InvalidDataException("The saved photo length has changed.");
                }
                var expiresOn = DateTimeOffset.UtcNow.AddHours(1);
                if (string.IsNullOrEmpty(properties.Value.VersionId))
                {
                    preview.Uri = (blob.CanGenerateSasUri
                                                                ? blob.GenerateSasUri(BlobSasPermissions.Read, expiresOn)
                                                                : await blob.GenerateUserDelegationReadOnlySasUri(expiresOn)).ToString();
                }
                else
                {
                    BlobSasBuilder sas = new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
                    {
                        BlobContainerName = blob.BlobContainerName,
                        BlobName = blob.Name,
                        BlobVersionId = properties.Value.VersionId,
                        Resource = "bv"
                    };
                    if (blob.CanGenerateSasUri)
                    {
                        preview.Uri = blob.WithVersion(properties.Value.VersionId).GenerateSasUri(sas).ToString();
                    }
                    else
                    {
                        var service = blob.GetParentBlobContainerClient().GetParentBlobServiceClient();
                        var key = await service.GetUserDelegationKeyAsync(null, expiresOn, ct);
                        preview.Uri = new BlobUriBuilder(blob.Uri)
                        {
                            VersionId = properties.Value.VersionId,
                            Sas = sas.ToSasQueryParameters(key.Value, service.AccountName)
                        }.ToUri().ToString();
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is RequestFailedException { Status: 404 or 412 })
            {
                preview.CanModerate = false;
                preview.Warning = string.Join(" ", new[] { preview.Warning, $"Photo {photo.Metadata.PhotoNumber + 1} is missing, changed or invalid. Repair the stored report before moderation." }.Where(s => s is not null));
                logger.LogWarning(ex, "Unavailable preview for {PhotoId}", photo.Metadata.PhotoId);
            }
        }

        private static bool SafeSegment(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                                    value.Length <= 256 && value is not "." and not ".." &&
                                    value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
        }

        private static void ValidatePhotoReference(SubmissionPhoto photo)
        {
            if (!photo.BlobName.StartsWith(FinalizedUploadPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid stored JPEG reference.");
            }
            var parts = photo.BlobName[FinalizedUploadPrefix.Length..].Split('/');
            if (string.IsNullOrWhiteSpace(photo.ETag) || photo.Length <= 0 ||
                !photo.BlobName.EndsWith(".jpeg", StringComparison.Ordinal) || !parts.All(SafeSegment) ||
                !(parts.Length == 1 || parts.Length == 4 &&
                    photo.BlobName.StartsWith(ReportStore.PhotoPrefix, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Invalid stored JPEG reference.");
            }
        }

        private async Task<List<LegacyGroup>> ReadLegacyGroupsAsync(CancellationToken ct)
        {
            Dictionary<string, LegacyGroup> groups = new Dictionary<string, LegacyGroup>(StringComparer.Ordinal);
            bool unidentifiedCorruption = false;
            await foreach (var blob in blobContainerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, FinalizedUploadPrefix, ct))
            {
                // Only flat per-photo sidecars use the original format, not nested report records.
                if (!blob.Name.StartsWith(FinalizedUploadPrefix, StringComparison.Ordinal) ||
                    blob.Name[FinalizedUploadPrefix.Length..].Contains('/') ||
                    !blob.Name.EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }
                FinalizedPhotoUploadMetadata? metadata = null;
                string? submissionId = null;
                string? error = null;
                string version = blob.Properties?.ETag?.ToString() ?? "";
                try
                {
                    var content = await blobContainerClient.GetBlobClient(blob.Name).DownloadContentAsync(ct);
                    version = content.Value.Details.ETag.ToString();
                    var json = Newtonsoft.Json.Linq.JObject.Parse(content.Value.Content.ToString());
                    var submissionToken = json.GetValue("SubmissionId", StringComparison.OrdinalIgnoreCase);
                    submissionId = submissionToken?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string?)submissionToken : null;
                    metadata = json.ToObject<FinalizedPhotoUploadMetadata>();
                    if (metadata is null || string.IsNullOrWhiteSpace(submissionId) || !SafeSegment(metadata.PhotoId) ||
                        blob.Name != $"{FinalizedUploadPrefix}{metadata.PhotoId}.json")
                    {
                        throw new InvalidDataException("Invalid legacy photo metadata or path.");
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException || ex is RequestFailedException { Status: 404 })
                {
                    error = $"Saved metadata {blob.Name} is missing or corrupt. Repair the original JSON/JPEG pair before moderation.";
                    logger.LogWarning(ex, "Invalid legacy sidecar {BlobName}", blob.Name);
                    unidentifiedCorruption |= string.IsNullOrWhiteSpace(submissionId);
                }

                submissionId = string.IsNullOrWhiteSpace(submissionId) ? $"unreadable:{blob.Name}" : submissionId;
                if (!groups.TryGetValue(submissionId, out var group))
                {
                    group = new LegacyGroup() { SubmissionId = submissionId };
                    groups.Add(submissionId, group);
                }
                group.Versions.Add($"{blob.Name}:{version}");
                group.Error ??= error;
                group.Metadata.Add(SanitizedCopy(metadata ?? new FinalizedPhotoUploadMetadata()
                {
                    SubmissionId = submissionId,
                    PhotoId = blob.Name,
                    PhotoNumber = group.Metadata.Count,
                    Tags = []
                }));
            }

            List<LegacyGroup> result = new List<LegacyGroup>();
            foreach (var group in groups.Values)
            {
                if (await reportStore.ExistsAsync(group.ReportId, ct))
                {
                    continue;
                }
                if (unidentifiedCorruption)
                {
                    group.Error ??= "Some saved JSON is unreadable and its report cannot be identified. Repair the flagged metadata before adopting legacy reports, so no photos are omitted.";
                }
                group.Metadata.Sort((a, b) => a.PhotoNumber.CompareTo(b.PhotoNumber));
                if (group.Metadata.Count > 4 || group.Metadata.Select(p => p.PhotoId).Distinct().Count() != group.Metadata.Count ||
                    !group.Metadata.Select(p => p.PhotoNumber).SequenceEqual(Enumerable.Range(0, group.Metadata.Count)))
                {
                    group.Error ??= "Saved photo numbering is incomplete or duplicated. Repair the original report before moderation.";
                }
                foreach (var metadata in group.Metadata)
                {
                    if (!SafeSegment(metadata.PhotoId))
                    {
                        continue;
                    }
                    try
                    {
                        string name = $"{FinalizedUploadPrefix}{metadata.PhotoId}.jpeg";
                        var properties = await blobContainerClient.GetBlobClient(name).GetPropertiesAsync(cancellationToken: ct);
                        SubmissionPhoto photo = new SubmissionPhoto(metadata, name, properties.Value.ETag.ToString(), properties.Value.ContentLength);
                        ValidatePhotoReference(photo);
                        group.Photos.Add(photo);
                        group.Versions.Add($"{name}:{photo.ETag}:{photo.Length}");
                    }
                    catch (Exception ex) when (ex is InvalidDataException || ex is RequestFailedException { Status: 404 })
                    {
                        group.Error ??= $"Saved photo {metadata.PhotoId} is missing or invalid. Restore the original JPEG before moderation.";
                        logger.LogWarning(ex, "Invalid legacy JPEG {PhotoId}", metadata.PhotoId);
                    }
                }
                result.Add(group);
            }
            return result;
        }

        private async Task TryCleanupAsync()
        {
            try
            {
                await reportStore.CleanupAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retired report cleanup will be retried.");
            }
        }

        private IActionResult ModerationError(Exception ex)
        {
            if (ex is ModerationConflictException or ReportConflictException or ReportInProgressException || ex is RequestFailedException { Status: 409 or 412 })
            {
                return Conflict(new
                {
                    message = "This report changed or has an operation in progress. Refresh pending reports before taking action."
                });
            }
            if (ex is ArgumentException or InvalidDataException)
            {
                return BadRequest(new
                {
                    message = ex.Message
                });
            }
            if (ex is KeyNotFoundException || ex is RequestFailedException { Status: 404 })
            {
                return NotFound(new
                {
                    message = "This pending report is no longer available. Refresh pending reports."
                });
            }
            logger.LogError(ex, "Could not acquire report moderation");
            return StatusCode(503, new
            {
                message = "Report storage is unavailable or moderation could not be confirmed. Refresh before retrying."
            });
        }

        private sealed class LegacyGroup
        {
            public required string SubmissionId { get; init; }
            public string ReportId => ReportStore.LegacyReportId(SubmissionId);
            public string Version => "legacy:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Versions.Order(StringComparer.Ordinal))))).ToLowerInvariant();
            public List<string> Versions { get; } = [];
            public List<FinalizedPhotoUploadMetadata> Metadata { get; } = [];
            public List<SubmissionPhoto> Photos { get; } = [];
            public string? Error { get; set; }
        }

        private sealed class ModerationConflictException : Exception
        {
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
                return BadRequest("Reading tweets from Twitter no longer works. Please copy/paste the tweet body, image links, and tweet link into the appropriate locations.");
            }
            else if (request.PostUrl.Contains("bsky.app"))
            {
                var fishyFlipClient = await blueskyClientProvider.GetFishyFlipClient();
                string atUrl = helperMethods.GetAtUrlFromBskyUrl(request.PostUrl, blueskyClient.Did!);
                ATUri atUri = ATUri.Create(atUrl);
                var (success, error) = await fishyFlipClient.GetPostsAsync([atUri]);
                if (error is not null || success is null)
                {
                    return BadRequest($"Unable to get post from {request.PostUrl}");
                }
                if (success.Posts.Count == 0)
                {
                    return BadRequest($"No posts found from {request.PostUrl}");
                }

                Post? blueskyPost = success.Posts[0].PostRecord;

                if (blueskyPost is null || blueskyPost.Text is null)
                {
                    return BadRequest($"No post found from {request.PostUrl}");
                }

                string skeetText = blueskyPost.Text;
                List<ReportedItem>? reportedItems = await helperMethods.TextToReportedItems(skeetText, mapsSearchClient);
                if (reportedItems == null)
                {
                    return BadRequest($"Couldn't find any reported items in skeet text.");
                }

                foreach (var reportedItem in reportedItems)
                {
                    reportedItem.CreatedAt = blueskyPost.CreatedAt ?? DateTime.UtcNow;
                    reportedItem.BlueskyLink = request.PostUrl;
                }

                List<string> imageLinks = new List<string>();
                switch (success.Posts[0].Embed)
                {
                    case ViewRecordDef viewRecordDef:
                        {
                            switch (viewRecordDef.Record)
                            {
                                case ViewRecord viewRecord:
                                    {
                                        if (viewRecord.Embeds is null || viewRecord.Embeds.Count == 0)
                                        {
                                            return BadRequest("No embeds found in skeet.");
                                        }
                                        switch (viewRecord.Embeds[0])
                                        {
                                            case ViewImages viewImages:
                                                {
                                                    imageLinks = viewImages.Images.Select(i => i.Fullsize).ToList();
                                                    break;
                                                }
                                            case ViewRecordWithMedia viewRecordWithMedia:
                                                {
                                                    ViewImages? recordMediaImages = viewRecordWithMedia.Media as ViewImages;
                                                    if (recordMediaImages == null)
                                                    {
                                                        logger.LogError($"Media embedded in record is not a type ViewImages for post {request.PostUrl}");
                                                        return BadRequest("Media embedded in record is not a type ViewImages");
                                                    }
                                                    imageLinks = recordMediaImages.Images.Select(i => i.Fullsize).ToList();
                                                    break;
                                                }
                                            default:
                                                break;
                                        }
                                        break;
                                    }
                                default:
                                    break;
                            }
                            break;
                        }
                    case ViewImages viewImages:
                        {
                            imageLinks = viewImages.Images.Select(i => i.Fullsize).ToList();
                            break;
                        }
                    case ViewRecordWithMedia viewRecordWithMedia:
                        {
                            ViewImages? recordMediaImages = viewRecordWithMedia.Media as ViewImages;
                            if (recordMediaImages == null)
                            {
                                logger.LogError($"Media embedded in record is not a type ViewImages for post {request.PostUrl}");
                                return BadRequest("Media embedded in record is not a type ViewImages");
                            }
                            imageLinks = recordMediaImages.Images.Select(i => i.Fullsize).ToList();
                            break;
                        }
                    default:
                        logger.LogError($"Unexpected Bluesky embed type {success.Posts[0].Embed!.Type} on post {request.PostUrl}");
                        break;
                }

                List<Stream> pictureStreams = new List<Stream>();
                foreach (var imageLink in imageLinks)
                {
                    Stream? pictureStream = await helperMethods.DownloadImage(imageLink, httpClient);
                    if (pictureStream == null)
                    {
                        return BadRequest($"Couldn't get stream from image link {imageLink}");
                    }
                    pictureStreams.Add(pictureStream);
                }

                if (pictureStreams.Count == 0)
                {
                    return BadRequest("No picture streams for skeet.");
                }

                // Upload the pictures to imgur
                await UploadImagesToImgur(reportedItems, pictureStreams);

                // Then upload to the three platforms
                Task mastodonUploadTask = UploadPostToMastodon(mastodonClient, reportedItems, pictureStreams, skeetText, skeetText);
                Task threadsUploadTask = UploadPostToThreads(threadsClient, reportedItems, imageLinks, skeetText);
                await Task.WhenAll(mastodonUploadTask, threadsUploadTask);

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

            return NoContent();
        }

        private async Task UploadPostToMastodon(MastodonClient mastodonClient,
            List<ReportedItem> reportedItems,
            List<Stream> pictureStreams,
            string mastodonText,
            string originalPostBody,
            bool failOnError = false)
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
                    DateTimeOffset processingDeadline = DateTimeOffset.UtcNow.AddMinutes(2);
                    do
                    {
                        attachment = await mastodonClient.GetAttachment(attachmentId);
                        if (failOnError && attachment is null && DateTimeOffset.UtcNow >= processingDeadline)
                        {
                            throw new TimeoutException("Mastodon media processing was not confirmed.");
                        }
                        await Task.Delay(500);
                    }
                    while (attachment == null);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to upload image to Mastodon. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Text {originalPostBody}");
                    if (failOnError)
                    {
                        throw;
                    }
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
                if (failOnError)
                {
                    throw;
                }
                return;
            }
        }

        private async Task UploadPostToBluesky(AtProtoClient blueskyClient,
            List<ReportedItem> reportedItems,
            List<Stream> pictureStreams,
            string skeetBody,
            List<BskyFacet> facets,
            string originalPostBody,
            bool failOnError = false)
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
                    if (failOnError)
                    {
                        throw;
                    }
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
                if (failOnError)
                {
                    throw;
                }
                return;
            }
        }

        private async Task UploadPostToThreads(ThreadsClient threadsClient,
            List<ReportedItem> reportedItems,
            List<string> tweetImageLinks,
            string threadsBody,
            bool failOnError = false)
        {
            try
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

                    // Each carousel item container must finish processing before it can be attached to the carousel
                    // container, otherwise Threads rejects them as "invalid, nonexistent, or expired".
                    foreach (var containerId in containerIds)
                    {
                        var itemContainerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, containerId);
                        if (itemContainerStatus.Status != "FINISHED")
                        {
                            logger.LogError($"Failed to publish Threads status. Carousel item container {containerId} was in status {itemContainerStatus.Status} instead of FINISHED. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Text {threadsBody}");
                            if (failOnError)
                            {
                                throw new InvalidOperationException("Threads carousel processing was not confirmed.");
                            }
                            return;
                        }
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
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to publish Threads status. Imgur links: {string.Join(' ', reportedItems[0].ImgurUrls)} Text {threadsBody}");
                if (failOnError)
                {
                    throw;
                }
                return;
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

            if (reportedItem.ThreadsLink != null)
            {
                ThreadsMediaObject? mediaObject = await threadsClient.GetUserScopedThreadsMediaObjectFromUrl(reportedItem.ThreadsLink);
                if (mediaObject != null)
                {
                    var response = await threadsClient.DeleteThreadsMediaObject(mediaObject.Id);
                    if (!response.Success)
                    {
                        logger.LogWarning($"Could not delete Threads post {reportedItem.ThreadsLink} for {reportedItem.TweetId}");
                    }
                }
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

            bool skipWorstIntersection = false;
            if (worstIntersectionItems.Count == 0)
            {
                skipWorstIntersection = true;
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
                if (!skipWorstIntersection)
                {
                    Console.WriteLine($"T3: {worstIntersectionText}");
                }
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
                if (!skipWorstIntersection)
                {
                    MastodonStatus thirdToot = await mastodonClient.PublishStatus($"{worstIntersectionText}", inReplyToId: secondToot.Id);
                }
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
                    string firstSkeetAtUri = GetAtUriLinkFromBlueskyLink(firstSkeetLink);
                    GetFeedPostsResponse mostCarsBlueskySkeetList = await blueskyClient.GetFeedPosts(new List<string>() { firstSkeetAtUri });
                    BskyPostView<BskyPost> mostCarsBlueskySkeet = mostCarsBlueskySkeetList.Posts[0];
                    BskyFacet firstSkeetFacet = new BskyFacet()
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
                    firstSkeet = await blueskyClient.CreateRecord(new CreateRecordRequest<BskyPost<BskyRecord>>()
                    {
                        Repo = blueskyClient.Did!,
                        Collection = BskyPost.Type,
                        Record = new BskyPost<BskyRecord>()
                        {
                            Text = firstSkeetText,
                            CreatedAt = DateTime.UtcNow,
                            Facets = new List<BskyFacet>()
                            {
                                firstSkeetFacet
                            },
                            Embed = new BskyRecord()
                            {
                                Record = new BskyViewRecord()
                                {
                                    Uri = mostCarsBlueskySkeet.Uri,
                                    Cid = mostCarsBlueskySkeet.Cid
                                }
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
                if (!skipMostCars)
                {
                    if (mostCars.Count > 1)
                    {
                        for (int i = 1; i < mostCars.Count; i++)
                        {
                            var item = mostCars[i];
                            string latestSkeetText = $"{mostCarsText} ";
                            string latestSkeetLink = GetSocialLinkForBluesky(item)!;
                            string latestSkeetAtUri = GetAtUriLinkFromBlueskyLink(latestSkeetLink);
                            GetFeedPostsResponse latestSkeetBlueskySkeetList = await blueskyClient.GetFeedPosts(new List<string>() { latestSkeetAtUri });
                            BskyPostView<BskyPost> latestSkeetBlueskySkeet = latestSkeetBlueskySkeetList.Posts[0];
                            BskyFacet latestSkeetFacet = new BskyFacet()
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
                            latestSkeet = await blueskyClient.CreateRecord(new CreateRecordRequest<BskyPost<BskyRecord>>()
                            {
                                Repo = blueskyClient.Did!,
                                Collection = BskyPost.Type,
                                Record = new BskyPost<BskyRecord>()
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
                                            Cid = firstSkeet.Cid,
                                            Uri = firstSkeet.Uri
                                        },
                                        Parent = new AtProtoStrongRef()
                                        {
                                            Cid = latestSkeet.Cid,
                                            Uri = latestSkeet.Uri
                                        }
                                    },
                                    Embed = new BskyRecord()
                                    {
                                        Record = new BskyViewRecord()
                                        {
                                            Uri = latestSkeetBlueskySkeet.Uri,
                                            Cid = latestSkeetBlueskySkeet.Cid
                                        }
                                    }
                                }
                            });
                        }
                    }
                }
                string secondSkeetText = $"{mostRidiculousText} ";
                string secondSkeetLink = GetSocialLinkForBluesky(mostRidiculousReportedItem)!;
                string secondSkeetAtUri = GetAtUriLinkFromBlueskyLink(secondSkeetLink);
                GetFeedPostsResponse secondSkeetList = await blueskyClient.GetFeedPosts(new List<string>() { secondSkeetAtUri });
                BskyPostView<BskyPost> secondSkeetPost = secondSkeetList.Posts[0];
                BskyFacet secondSkeetFacet = new BskyFacet()
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
                CreateRecordResponse secondSkeet = await blueskyClient.CreateRecord(new CreateRecordRequest<BskyPost<BskyRecord>>()
                {
                    Repo = blueskyClient.Did!,
                    Collection = BskyPost.Type,
                    Record = new BskyPost<BskyRecord>()
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
                                Cid = firstSkeet.Cid,
                                Uri = firstSkeet.Uri
                            },
                            Parent = new AtProtoStrongRef()
                            {
                                Cid = latestSkeet.Cid,
                                Uri = latestSkeet.Uri
                            }
                        },
                        Embed = new BskyRecord()
                        {
                            Record = new BskyViewRecord()
                            {
                                Uri = secondSkeetPost.Uri,
                                Cid = secondSkeetPost.Cid
                            }
                        }
                    }
                });
                latestSkeet = secondSkeet;

                if (!skipWorstIntersection)
                {
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
                                    Cid = firstSkeet.Cid,
                                    Uri = firstSkeet.Uri
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

                if (!skipWorstIntersection)
                {
                    string thirdCreationId = await threadsClient.CreateThreadsMediaContainer("TEXT",
                                                            $"{worstIntersectionText}",
                                                            replyToId: secondThreadsPostId);
                    var thirdContainerStatus = await helperMethods.WaitForThreadsMediaContainer(threadsClient, thirdCreationId);
                    if (thirdContainerStatus.Status == "FINISHED")
                    {
                        string thirdThreadsPostId = await threadsClient.PublishThreadsMediaContainer(thirdCreationId);
                    }
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

        private string GetAtUriLinkFromBlueskyLink(string blueskyLink)
        {
            if (!blueskyLink.StartsWith("https://bsky.app"))
            {
                throw new Exception($"Unexpected link. {blueskyLink} Bluesky link must start with https://bsky.app");
            }

            string[] splitBlueskyLink = blueskyLink.Split('/');
            string did = splitBlueskyLink[4];
            if (did == "seattle.carinbikelane.com")
            {
                did = "did:plc:na7rhfhytqgjqebzr4j54jrw";
            }
            string rKey = splitBlueskyLink.Last();
            return $"at://{did}/app.bsky.feed.post/{rKey}";
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

        private async Task<List<string>> UploadImagesToImgur(List<ReportedItem> reportedItems, List<Stream> pictureStreams,
            Action? externalEffectsStarting = null)
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
                using MemoryStream imgurStream = new MemoryStream();
                await stream.CopyToAsync(imgurStream);
                imgurStream.Position = 0;
                stream.Position = 0;
                IImage imgurUpload;
                try
                {
                    // Note there is a bug in the ImgurAPI package where when a rate limit is hit an exception
                    // will be thrown because the package doesn't process rate limits correctly.
                    // A timeout after this boundary can still mean the upload was accepted.
                    externalEffectsStarting?.Invoke();
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
            public string? ReportVersion { get; set; }
            public string? LegacySubmissionId { get; set; }
            public string ModerationStatus { get; set; } = "pending";
            public string? ModerationOperationId { get; set; }
            public DateTimeOffset? ModerationStartedAt { get; set; }
            public string? Warning { get; set; }
            public bool CanModerate { get; set; } = true;

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
                string? mastodonFullUsername = null,
                string? mastodonAccessToken = null,
                string? blueskyHandle = null,
                string? blueskyUserDid = null,
                string? threadsUsername = null,
                string? threadsAccessToken = null,
                string? twitterLink = null,
                string? blueskyAdminDid = null,
                string? blueskyAccessJwt = null) :
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
                    mastodonFullUsername,
                    mastodonAccessToken,
                    blueskyHandle,
                    blueskyUserDid,
                    threadsUsername,
                    threadsAccessToken,
                    twitterLink,
                    blueskyAdminDid,
                    blueskyAccessJwt)
            {
            }
        }

        public sealed class ModerateReportRequest
        {
            public string ReportId { get; set; } = "";
            public string ReportVersion { get; set; } = "";
            public string? LegacySubmissionId { get; set; }
            public List<string> PhotoIds { get; set; } = [];
            public AdminPublicationEdits? Edits { get; set; }
            public string? BlueskyAdminDid { get; set; }
            public string? BlueskyAccessJwt { get; set; }
        }

        public sealed class AdminPublicationEdits
        {
            public int NumberOfCars { get; set; }
            public DateTime PhotoDateTime { get; set; }
            public string PhotoCrossStreet { get; set; } = "";
            public string PhotoLatitude { get; set; } = "";
            public string PhotoLongitude { get; set; } = "";
            public string? TwitterSubmittedBy { get; set; }
            public string? MastodonSubmittedBy { get; set; }
            public string? BlueskySubmittedBy { get; set; }
            public string? ThreadsSubmittedBy { get; set; }
            public string? TwitterLink { get; set; }
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
