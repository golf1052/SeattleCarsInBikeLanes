using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using golf1052.atproto.net.Models.AtProto.Repo;
using golf1052.atproto.net.Models.Bsky.Feed;
using golf1052.atproto.net.Models.Bsky.Richtext;
using golf1052.Mastodon.Models.Statuses;
using golf1052.Mastodon.Models.Statuses.Media;
using golf1052.ThreadsAPI.Models;
using Imgur.API.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Newtonsoft.Json;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;
using static SeattleCarsInBikeLanes.Controllers.AdminPageController;

namespace SeattleCarsInBikeLanes.Tests
{
    public partial class AdminPageControllerTests
    {
        private const string ReportId = "0123456789abcdef0123456789abcdef";

        private static SubmissionReport SavedReport(int count = 2)
        {
            var photos = Enumerable.Range(0, count).Select(index =>
                                    {
                                        FinalizedPhotoUploadMetadata metadata = new FinalizedPhotoUploadMetadata()
                                        {
                                            ReportId = ReportId,
                                            SubmissionId = "original-submission",
                                            PhotoId = $"photo-{index}",
                                            PhotoNumber = index,
                                            NumberOfCars = 1,
                                            PhotoDateTime = new DateTime(2026, 9, 1, 12, 30, 0),
                                            PhotoLatitude = "47.62",
                                            PhotoLongitude = "-122.33",
                                            PhotoCrossStreet = "Original street",
                                            Tags = [new ImageTag() { Name = "bicycle", Confidence = .9f }],
                                            BlueskyUserDid = "did:plc:original",
                                            BlueskyHandle = "original.example",
                                            TwitterSubmittedBy = "Submission",
                                            MastodonSubmittedBy = "Submission",
                                            BlueskySubmittedBy = "Submission",
                                            ThreadsSubmittedBy = "Submission"
                                        };
                                        return new SubmissionPhoto(metadata, $"{ReportStore.PhotoPrefix}{ReportId}/attempt/{index}.jpeg", "\"photo-version\"", 3);
                                    }).ToList();
            return new SubmissionReport(new SubmissionReceipt(ReportId, "original-submission", DateTimeOffset.UtcNow,
                new ReportAttribution(BlueskyDid: "did:plc:original")), "installation", photos)
            {
                Version = "\"report-version\""
            };
        }

        private static ModerateReportRequest RequestFor(SubmissionReport report)
        {
            return new ModerateReportRequest()
            {
                ReportId = report.Receipt.ReportId,
                ReportVersion = report.Version!,
                PhotoIds = report.Photos.Select(p => p.Metadata.PhotoId).ToList(),
                Edits = new AdminPublicationEdits()
                {
                    NumberOfCars = 3,
                    PhotoDateTime = new DateTime(2026, 9, 2, 13, 45, 0),
                    PhotoLatitude = "47.61",
                    PhotoLongitude = "-122.32",
                    PhotoCrossStreet = "Corrected street",
                    TwitterSubmittedBy = "Corrected Twitter text",
                    MastodonSubmittedBy = "Corrected Mastodon text",
                    BlueskySubmittedBy = "Corrected Bluesky text",
                    ThreadsSubmittedBy = "Corrected Threads text",
                    TwitterLink = "https://x.com/example/status/1"
                }
            };
        }

        private void SetupReport(SubmissionReport report, Action<IReadOnlyList<FinalizedPhotoUploadMetadata>?>? inspect = null)
        {
            mockReportStore.Setup(s => s.GetForModerationAsync(report.Receipt.ReportId, It.IsAny<CancellationToken>()))
                                        .ReturnsAsync(report);
            mockReportStore.Setup(s => s.BeginModerationAsync(report.Receipt.ReportId, It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<FinalizedPhotoUploadMetadata>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, string kind, IReadOnlyList<FinalizedPhotoUploadMetadata>? edits, string? version, CancellationToken ct) =>
                {
                    Assert.Equal(report.Version, version);
                    inspect?.Invoke(edits);
                    return report with
                    {
                        Photos = report.Photos.Select((p, i) => p with { Metadata = edits?[i] ?? p.Metadata }).ToList(),
                        Moderation = new ModerationOperation("operation", kind, DateTimeOffset.UtcNow),
                        Version = "\"owned\""
                    };
                });
        }

        private Mock<BlobClient> SetupJpeg(SubmissionPhoto photo)
        {
            Mock<BlobClient> blob = new Mock<BlobClient>();
            mockBlobContainerClient!.Setup(c => c.GetBlobClient(photo.BlobName)).Returns(blob.Object);
            blob.Setup(b => b.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobProperties(contentLength: photo.Length,
                    eTag: new ETag(photo.ETag)), Mock.Of<Response>()));
            blob.Setup(b => b.DownloadContentAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
                .Callback<BlobDownloadOptions, CancellationToken>((options, _) => Assert.Equal(photo.ETag, options.Conditions.IfMatch.ToString()))
                .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobDownloadResult(
                    BinaryData.FromBytes(new byte[] { 1, 2, 3 })), Mock.Of<Response>()));
            blob.SetupGet(b => b.CanGenerateSasUri).Returns(true);
            blob.Setup(b => b.GenerateSasUri(BlobSasPermissions.Read, It.IsAny<DateTimeOffset>()))
                .Returns(new Uri($"https://storage.example/{photo.BlobName}?sig=test"));
            return blob;
        }

        private void SetupPending(params SubmissionReport[] reports)
        {
            mockReportStore.Setup(s => s.GetPendingAsync(It.IsAny<CancellationToken>())).Returns(Enumerate(reports));
            SetupLegacyListing([]);
        }

        private static async IAsyncEnumerable<SubmissionReport> Enumerate(IEnumerable<SubmissionReport> reports)
        {
            await Task.CompletedTask;
            foreach (var report in reports)
            {
                yield return report;
            }
        }

        private void SetupLegacyListing(IReadOnlyList<(string Name, string Json)> sidecars)
        {
            var blobs = sidecars.Select(pair => BlobsModelFactory.BlobItem(name: pair.Name)).ToList();
            mockBlobContainerClient!.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "finalizedupload/", It.IsAny<CancellationToken>()))
                .Returns(AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(blobs, null, Mock.Of<Response>())]));
            foreach (var pair in sidecars)
            {
                Mock<BlobClient> blob = new Mock<BlobClient>();
                blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobDownloadResult(BinaryData.FromString(pair.Json),
                        BlobsModelFactory.BlobDownloadDetails(eTag: new ETag("\"sidecar-version\""))), Mock.Of<Response>()));
                mockBlobContainerClient.Setup(c => c.GetBlobClient(pair.Name)).Returns(blob.Object);
            }
        }

        private SubmissionReport SetupLegacy(int count = 2)
        {
            var original = SavedReport(count);
            string id = ReportStore.LegacyReportId(original.Receipt.SubmissionId);
            var report = original with
            {
                Receipt = original.Receipt with
                {
                    ReportId = id
                },
                DeviceId = null,
                Photos = original.Photos.Select(p => p with { BlobName = $"finalizedupload/{p.Metadata.PhotoId}.jpeg" }).ToList()
            };
            SetupPending();
            SetupLegacyListing(report.Photos.Select(p => ($"finalizedupload/{p.Metadata.PhotoId}.json", JsonConvert.SerializeObject(p.Metadata))).ToList());
            foreach (var photo in report.Photos)
            {
                SetupJpeg(photo);
            }

            return report;
        }

        [Fact]
        public async Task Pending_UsesJpegReferencesAndShowsOwnerWithoutSecrets()
        {
            var report = SavedReport() with
            {
                Moderation = new ModerationOperation("owner", "publishing", DateTimeOffset.UtcNow)
            };
            report.Photos[0].Metadata.BlueskyAccessJwt = "secret";
            SetupPending(report);
            var jpegs = report.Photos.Select(SetupJpeg).ToList();
            var pending = await controller!.GetPendingPhotos();
            var photos = pending[ReportId];
            Assert.Equal(2, photos.Count);
            Assert.All(photos, photo =>
            {
                Assert.Equal(report.Version, photo.ReportVersion);
                Assert.Equal("installation", photo.DeviceId);
                Assert.Equal("publishing", photo.ModerationStatus);
                Assert.Equal("owner", photo.ModerationOperationId);
                Assert.False(photo.CanModerate);
                Assert.StartsWith("https://storage.example/", photo.Uri);
                Assert.Contains("in progress", photo.Warning);
            });
            string json = JsonConvert.SerializeObject(pending);
            Assert.DoesNotContain("secret", json);
            Assert.DoesNotContain("base64", json);
            foreach (var jpeg in jpegs)
            {
                jpeg.Verify(b => b.DownloadContentAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
            }

            mockReportStore.Verify(s => s.AdoptLegacyAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SubmissionPhoto>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task LegacyListing_IsReadOnlyAndPreservesOriginalGrouping()
        {
            var report = SetupLegacy();
            var pending = await controller!.GetPendingPhotos();
            var photos = Assert.Single(pending).Value;
            Assert.Equal(2, photos.Count);
            Assert.Equal([0, 1], photos.Select(p => p.PhotoNumber));
            Assert.All(photos, photo =>
            {
                Assert.Equal(report.Receipt.SubmissionId, photo.SubmissionId);
                Assert.Equal(report.Receipt.ReportId, photo.ReportId);
                Assert.StartsWith("legacy:", photo.ReportVersion);
                Assert.True(photo.CanModerate);
            });
            mockReportStore.Verify(s => s.AdoptLegacyAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SubmissionPhoto>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Pending_SharedFinalizedRootDoesNotInterpretNestedRecordsAsPhotoSidecars()
        {
            var existing = SetupLegacy(1);
            var current = SavedReport(1);
            foreach (var photo in current.Photos)
            {
                SetupJpeg(photo);
            }

            mockReportStore.Setup(s => s.GetPendingAsync(It.IsAny<CancellationToken>())).Returns(Enumerate([current]));
            string record = $"{ReportStore.BlobPrefix}{ReportId}.json";
            string nested = $"{ReportStore.PhotoPrefix}{ReportId}/attempt/unrelated.json";
            SetupLegacyListing([
                ($"finalizedupload/{existing.Photos[0].Metadata.PhotoId}.json",
                    JsonConvert.SerializeObject(existing.Photos[0].Metadata)),
                (record, "{not a per-photo sidecar"),
                (nested, "{not a per-photo sidecar")
            ]);

            var pending = await controller!.GetPendingPhotos();

            Assert.Equal(2, pending.Count);
            Assert.True(Assert.Single(pending[existing.Receipt.ReportId]).CanModerate);
            Assert.True(Assert.Single(pending[ReportId]).CanModerate);
            mockBlobContainerClient!.Verify(c => c.GetBlobClient(record), Times.Never);
            mockBlobContainerClient.Verify(c => c.GetBlobClient(nested), Times.Never);
        }

        [Fact]
        public async Task LegacyDelete_AdoptsFullServerReadSetBeforeOwnershipAndRetirement()
        {
            var report = SetupLegacy();
            SetupReport(report);
            var pending = (await controller!.GetPendingPhotos())[report.Receipt.ReportId];
            var request = RequestFor(report);
            request.ReportVersion = pending[0].ReportVersion!;
            request.LegacySubmissionId = report.Receipt.SubmissionId;
            bool adopted = false, owned = false, retired = false;
            mockReportStore.Setup(s => s.AdoptLegacyAsync(report.Receipt.SubmissionId, It.IsAny<IReadOnlyList<SubmissionPhoto>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, IReadOnlyList<SubmissionPhoto> photos, CancellationToken _) =>
                {
                    Assert.Equal(report.Photos.Select(p => p.BlobName), photos.Select(p => p.BlobName));
                    Assert.All(photos, p =>
                    {
                        Assert.Equal(3, p.Length);
                        Assert.Equal("\"photo-version\"", p.ETag);
                    });
                    adopted = true;
                    return report;
                });
            mockReportStore.Setup(s => s.BeginModerationAsync(report.Receipt.ReportId, "deleting", null, report.Version, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Assert.True(adopted);
                    owned = true;
                    return report with
                    {
                        Moderation = new ModerationOperation("operation", "deleting", DateTimeOffset.UtcNow)
                    };
                });
            mockReportStore.Setup(s => s.RetireAsync(report.Receipt.ReportId, "operation", It.IsAny<CancellationToken>()))
                .Callback(() =>
                {
                    Assert.True(owned);
                    retired = true;
                }).Returns(Task.CompletedTask);
            mockReportStore.Setup(s => s.CleanupAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Assert.True(retired)).Returns(Task.CompletedTask);
            Assert.IsType<NoContentResult>(await controller.DeletePendingPhoto(request));
            Assert.True(retired);
        }

        [Fact]
        public async Task LegacyPublish_UsesTheSameJpegPublicationPipelineAfterAdoption()
        {
            var report = SetupLegacy(1);
            SetupPublication(report);
            var photos = (await controller!.GetPendingPhotos())[report.Receipt.ReportId];
            mockReportStore.Setup(s => s.AdoptLegacyAsync(report.Receipt.SubmissionId,
                It.IsAny<IReadOnlyList<SubmissionPhoto>>(), It.IsAny<CancellationToken>())).ReturnsAsync(report);
            var request = RequestFor(report);
            request.ReportVersion = photos[0].ReportVersion!;
            request.LegacySubmissionId = report.Receipt.SubmissionId;
            Assert.IsType<NoContentResult>(await controller.UploadTweet(request));
            mockReportStore.Verify(s => s.AdoptLegacyAsync(report.Receipt.SubmissionId,
                It.IsAny<IReadOnlyList<SubmissionPhoto>>(), It.IsAny<CancellationToken>()), Times.Once);
            mockReportedItemsDatabase!.Verify(d => d.SavePublishedReportAsync(It.Is<ReportedItem>(item =>
                item.TweetId == report.Receipt.ReportId + ".0" && item.NumberOfCars == 3),
                It.IsAny<CancellationToken>()), Times.Once);
            mockReportStore.Verify(s => s.RetireAsync(report.Receipt.ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task LegacyTombstone_SuppressesSidecarsRemainingAfterCleanup()
        {
            var report = SetupLegacy();
            mockReportStore.Setup(s => s.ExistsAsync(report.Receipt.ReportId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Assert.Empty(await controller!.GetPendingPhotos());
        }

        [Fact]
        public async Task LegacyMissingJpeg_IsVisibleAndCannotBeAdopted()
        {
            var report = SetupLegacy();
            var missing = SetupJpeg(report.Photos[1]);
            missing.Setup(b => b.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(404, "missing"));
            var photos = (await controller!.GetPendingPhotos())[report.Receipt.ReportId];
            Assert.Equal(2, photos.Count);
            Assert.All(photos, p => Assert.False(p.CanModerate));
            Assert.Contains("missing", photos[0].Warning);
            var request = RequestFor(report);
            request.ReportVersion = photos[0].ReportVersion!;
            request.LegacySubmissionId = report.Receipt.SubmissionId;
            Assert.IsType<BadRequestObjectResult>(await controller.DeletePendingPhoto(request));
            mockReportStore.Verify(s => s.AdoptLegacyAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SubmissionPhoto>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task LegacyCorruptJson_IsVisibleRatherThanSilentlyDropped()
        {
            SetupPending();
            SetupLegacyListing([("finalizedupload/broken.json", "{invalid")]);
            var photo = Assert.Single(Assert.Single(await controller!.GetPendingPhotos()).Value);
            Assert.False(photo.CanModerate);
            Assert.Contains("corrupt", photo.Warning);
        }

        [Fact]
        public async Task LegacyAdoptedSinceListing_RequiresRefreshRatherThanOverwritingTheSharedReport()
        {
            var report = SetupLegacy();
            var photos = (await controller!.GetPendingPhotos())[report.Receipt.ReportId];
            mockReportStore.Setup(s => s.ExistsAsync(report.Receipt.ReportId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var request = RequestFor(report);
            request.ReportVersion = photos[0].ReportVersion!;
            request.LegacySubmissionId = report.Receipt.SubmissionId;
            Assert.IsType<ConflictObjectResult>(await controller.DeletePendingPhoto(request));
            mockReportStore.Verify(s => s.AdoptLegacyAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SubmissionPhoto>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Pending_ChangedJpegIsVisibleAndDisablesTheWholeReport()
        {
            var report = SavedReport();
            SetupPending(report);
            foreach (var photo in report.Photos)
            {
                SetupJpeg(photo);
            }

            SetupJpeg(report.Photos[0]).Setup(b => b.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(412, "changed"));
            var photos = (await controller!.GetPendingPhotos())[ReportId];
            Assert.All(photos, p => Assert.False(p.CanModerate));
            Assert.Contains("missing, changed or invalid", photos[0].Warning);
        }

        [Fact]
        public async Task Pending_PinsPreviewToBlobVersionWhenStorageVersioningIsAvailable()
        {
            var report = SavedReport(1);
            SetupPending(report);
            var photo = report.Photos[0];
            Mock<BlobClient> blob = new Mock<BlobClient>(new Uri("https://storage.example/container/photo.jpeg"),
                new Azure.Storage.StorageSharedKeyCredential("storage", Convert.ToBase64String(new byte[32])),
                new BlobClientOptions())
            {
                CallBase = true
            };
            mockBlobContainerClient!.Setup(c => c.GetBlobClient(photo.BlobName)).Returns(blob.Object);
            blob.Setup(b => b.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobProperties(contentLength: 3,
                    eTag: new ETag(photo.ETag), versionId: "version-1"), Mock.Of<Response>()));
            string uri = (await controller!.GetPendingPhotos())[ReportId][0].Uri!;
            Assert.Contains("versionid=version-1", uri);
            Assert.Contains("sr=bv", uri);
            Assert.Contains("sp=r", uri);
        }

        [Fact]
        public async Task Delete_RejectsPartialOrForgedPhotoSet()
        {
            var report = SavedReport();
            SetupReport(report);
            var request = RequestFor(report);
            request.PhotoIds = ["../../other-report"];
            Assert.IsType<BadRequestObjectResult>(await controller!.DeletePendingPhoto(request));
            mockReportStore.Verify(s => s.BeginModerationAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FinalizedPhotoUploadMetadata>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            mockReportStore.Verify(s => s.RetireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData("publishing")]
        [InlineData("deleting")]
        public async Task Moderation_ConflictsDoNotStartExternalWorkOrRetire(string kind)
        {
            var report = SavedReport();
            SetupReport(report);
            mockReportStore.Setup(s => s.BeginModerationAsync(ReportId, kind, It.IsAny<IReadOnlyList<FinalizedPhotoUploadMetadata>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ReportConflictException("stale or owned"));
            var result = kind == "publishing" ? await controller!.UploadTweet(RequestFor(report)) : await controller!.DeletePendingPhoto(RequestFor(report));
            Assert.IsType<ConflictObjectResult>(result);
            mockMastodonClientProvider!.Verify(p => p.GetServerClient(), Times.Never);
            mockReportStore.Verify(s => s.RetireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Publish_SnapshotAllowsCorrectionsButPreservesIdentityReceiptAndStripsSecrets()
        {
            var report = SavedReport();
            foreach (var photo in report.Photos)
            {
                photo.Metadata.TwitterAccessToken = "twitter-secret";
                photo.Metadata.MastodonAccessToken = "mastodon-secret";
                photo.Metadata.ThreadsAccessToken = "threads-secret";
                photo.Metadata.BlueskyAccessJwt = "bluesky-secret";
                photo.Metadata.BlueskyAdminDid = "admin-secret";
            }
            bool inspected = false;
            SetupReport(report, edits =>
            {
                inspected = true;
                Assert.Equal(2, edits!.Count);
                Assert.All(edits, edit =>
                {
                    Assert.Equal("did:plc:original", edit.BlueskyUserDid);
                    Assert.Equal("Corrected street", edit.PhotoCrossStreet);
                    Assert.Equal(3, edit.NumberOfCars);
                    Assert.Equal("Corrected Bluesky text", edit.BlueskySubmittedBy);
                    Assert.Equal("https://x.com/example/status/1", edit.TwitterLink);
                    Assert.Single(edit.Tags);
                    Assert.DoesNotContain("secret", JsonConvert.SerializeObject(edit));
                });
                Assert.Equal(report.Photos.Select(p => p.Metadata.PhotoId), edits.Select(p => p.PhotoId));
            });
            var request = RequestFor(report);
            request.BlueskyAdminDid = "request-only-did";
            request.BlueskyAccessJwt = "request-only-jwt";
            var browserJson = Newtonsoft.Json.Linq.JObject.FromObject(request);
            browserJson["Edits"]!["PhotoId"] = "../../forged";
            browserJson["Edits"]!["BlueskyUserDid"] = "did:plc:forged";
            browserJson["Edits"]!["DeviceId"] = "forged-device";
            browserJson["Edits"]!["TwitterAccessToken"] = "forged-secret";
            browserJson["Edits"]!["Tags"] = new Newtonsoft.Json.Linq.JArray();
            request = browserJson.ToObject<ModerateReportRequest>()!;
            mockMastodonClientProvider!.Setup(p => p.GetServerClient()).Throws(new InvalidOperationException("preflight"));
            var result = Assert.IsType<ObjectResult>(await controller!.UploadTweet(request));
            Assert.Equal(503, result.StatusCode);
            Assert.True(inspected);
            Assert.Equal("did:plc:original", report.Receipt.Attribution.BlueskyDid);
            Assert.All(report.Photos, p => Assert.Equal("Original street", p.Metadata.PhotoCrossStreet));
            mockReportStore.Verify(s => s.ReleaseModerationAsync(ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
            mockReportStore.Verify(s => s.RetireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Publish_InvalidGpsFailsBeforeClaim()
        {
            var report = SavedReport();
            var request = RequestFor(report);
            request.Edits!.PhotoLatitude = "NaN";
            Assert.IsType<BadRequestObjectResult>(await controller!.UploadTweet(request));
            mockReportStore.Verify(s => s.BeginModerationAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FinalizedPhotoUploadMetadata>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private void SetupPublication(SubmissionReport report)
        {
            SetupReport(report);
            foreach (var photo in report.Photos)
            {
                SetupJpeg(photo);
            }

            mockMastodonClientProvider!.Setup(p => p.GetServerClient()).Returns(mockMastodonClient!.Object);
            mockBlueskyClientProvider!.Setup(p => p.GetClient()).ReturnsAsync(mockBlueskyClient!.Object);
            mockImageEndpoint!.Setup(p => p.UploadImageAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<int>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Image() { Link = "https://imgur.example/photo.jpeg" });
            MastodonAttachment attachment = new MastodonAttachment() { Id = "attachment" };
            mockMastodonClient.Setup(p => p.UploadMedia(It.IsAny<Stream>())).ReturnsAsync(attachment);
            mockMastodonClient.Setup(p => p.GetAttachment(It.IsAny<string>())).ReturnsAsync(attachment);
            mockMastodonClient.Setup(p => p.PublishStatus(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new MastodonStatus() { Url = "https://mastodon.example/post" });
            mockBlueskyClient.Setup(p => p.UploadBlob(It.IsAny<UploadBlobRequest>())).ReturnsAsync(new UploadBlobResponse()
            {
                Blob = new AtProtoBlob() { Type = "blob", Ref = new AtProtoBlobRef() { Link = "blob" }, MimeType = "image/jpeg", Size = 3 }
            });
            mockBlueskyClient.Setup(p => p.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()))
                .ReturnsAsync(new CreateRecordResponse() { Cid = "cid", Uri = "at://did:plc:original/app.bsky.feed.post/post" });
            mockHelperMethods!.Setup(h => h.GetBlueskyPostUrl(It.IsAny<string>())).Returns("https://bsky.app/profile/example/post/post");
            mockReportedItemsDatabase!.Setup(d => d.SavePublishedReportAsync(It.IsAny<ReportedItem>(),
                It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        }

        [Fact]
        public async Task Publish_SocialProvidersOverlapWithIndependentReadOnlyPhotoStreams()
        {
            var report = SavedReport(2);
            SetupPublication(report);
            byte[][] images = [[1, 2, 3], [4, 5, 6]];
            for (int index = 0; index < images.Length; index++)
            {
                byte[] bytes = images[index];
                SetupJpeg(report.Photos[index])
                    .Setup(b => b.DownloadContentAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobDownloadResult(BinaryData.FromBytes(bytes)),
                        Mock.Of<Response>()));
            }
            static TaskCompletionSource Signal()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var imgurStarted = Signal();
            var mastodonStarted = Signal();
            var blueskyStarted = Signal();
            var threadsStarted = Signal();
            var blueskyFinished = Signal();
            var releaseImgur = Signal();
            var releaseMastodon = Signal();
            var releaseBluesky = Signal();
            var releaseThreads = Signal();
            int imgurUploads = 0;
            bool mastodonPosted = false, threadsPosted = false;
            List<Stream> mastodonStreams = new List<Stream>();
            List<Stream> blueskyStreams = new List<Stream>();
            mockImageEndpoint!.Setup(p => p.UploadImageAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<int>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    imgurStarted.TrySetResult();
                    await releaseImgur.Task;
                    imgurUploads++;
                    return new Image() { Link = "https://imgur.example/photo.jpeg" };
                });
            mockMastodonClient!.Setup(p => p.UploadMedia(It.IsAny<Stream>())).Returns(async (Stream stream) =>
            {
                Assert.Equal(2, imgurUploads);
                Assert.False(stream.CanWrite);
                int index = mastodonStreams.Count;
                mastodonStreams.Add(stream);
                Assert.Equal(images[index][0], stream.ReadByte());
                mastodonStarted.TrySetResult();
                await releaseMastodon.Task;
                using MemoryStream remainder = new MemoryStream();
                await stream.CopyToAsync(remainder);
                Assert.Equal(images[index][1..], remainder.ToArray());
                stream.Dispose();
                return new MastodonAttachment() { Id = "attachment" };
            });
            mockBlueskyClient!.Setup(p => p.UploadBlob(It.IsAny<UploadBlobRequest>()))
                .Returns(async (UploadBlobRequest request) =>
                {
                    Assert.Equal(2, imgurUploads);
                    Stream stream = request.Content;
                    Assert.False(stream.CanWrite);
                    int index = blueskyStreams.Count;
                    blueskyStreams.Add(stream);
                    blueskyStarted.TrySetResult();
                    await releaseBluesky.Task;
                    using MemoryStream bytes = new MemoryStream();
                    await stream.CopyToAsync(bytes);
                    Assert.Equal(images[index], bytes.ToArray());
                    stream.Dispose();
                    return new UploadBlobResponse()
                    {
                        Blob = new AtProtoBlob() { Type = "blob", Ref = new AtProtoBlobRef() { Link = "blob" }, MimeType = "image/jpeg", Size = 3 }
                    };
                });
            mockThreadsClient!.Setup(p => p.CreateThreadsMediaContainer(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<List<string>>()))
                .Returns(async () =>
                {
                    Assert.Equal(2, imgurUploads);
                    threadsStarted.TrySetResult();
                    await releaseThreads.Task;
                    return "001";
                });
            mockMastodonClient.Setup(p => p.PublishStatus(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<string>(), It.IsAny<string>()))
                .Callback(() => mastodonPosted = true)
                .ReturnsAsync(new MastodonStatus() { Url = "https://mastodon.example/post" });
            mockBlueskyClient.Setup(p => p.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()))
                .Callback(() => blueskyFinished.TrySetResult())
                .ReturnsAsync(new CreateRecordResponse() { Cid = "cid", Uri = "at://did:plc:original/app.bsky.feed.post/post" });
            mockThreadsClient.Setup(p => p.GetThreadsMediaObject(It.IsAny<string>(), It.IsAny<string>()))
                .Callback(() => threadsPosted = true)
                .ReturnsAsync(new ThreadsMediaObject() { Id = "002", Permalink = "https://threads.net/002" });
            mockReportedItemsDatabase!.Setup(d => d.SavePublishedReportAsync(It.IsAny<ReportedItem>(), It.IsAny<CancellationToken>()))
                .Callback(() => Assert.True(mastodonPosted && blueskyFinished.Task.IsCompleted && threadsPosted))
                .Returns(Task.CompletedTask);

            Task<IActionResult> publishing = controller!.UploadTweet(RequestFor(report));
            try
            {
                await imgurStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(mastodonStarted.Task.IsCompleted);
                Assert.False(blueskyStarted.Task.IsCompleted);
                Assert.False(threadsStarted.Task.IsCompleted);
                releaseImgur.TrySetResult();
                await Task.WhenAll(mastodonStarted.Task, blueskyStarted.Task, threadsStarted.Task)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.NotSame(mastodonStreams[0], blueskyStreams[0]);
                Assert.Equal(1, mastodonStreams[0].Position);
                Assert.Equal(0, blueskyStreams[0].Position);

                releaseBluesky.TrySetResult();
                await blueskyFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(mastodonStreams[0].CanRead);
                Assert.Equal(1, mastodonStreams[0].Position);
                mockReportedItemsDatabase.Verify(d => d.SavePublishedReportAsync(It.IsAny<ReportedItem>(),
                    It.IsAny<CancellationToken>()), Times.Never);
                releaseMastodon.TrySetResult();
                releaseThreads.TrySetResult();
                Assert.IsType<NoContentResult>(await publishing.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(2, mastodonStreams.Count);
                Assert.Equal(2, blueskyStreams.Count);
                Assert.All(mastodonStreams.Concat(blueskyStreams), stream => Assert.False(stream.CanRead));
            }
            finally
            {
                releaseImgur.TrySetResult();
                releaseMastodon.TrySetResult();
                releaseBluesky.TrySetResult();
                releaseThreads.TrySetResult();
                await publishing.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Theory]
        [InlineData("mastodon")]
        [InlineData("bluesky")]
        [InlineData("threads")]
        public async Task Publish_WaitsForEveryProviderBeforeReleasingAFailedAttempt(string failingProvider)
        {
            var report = SavedReport(1);
            SetupPublication(report);
            string[] providers = ["mastodon", "bluesky", "threads"];
            var started = providers.ToDictionary(name => name,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            var release = providers.ToDictionary(name => name,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            Stream? mastodonStream = null, blueskyStream = null;
            Task WaitForProvider(string name)
            {
                started[name].TrySetResult();
                return name == failingProvider
                    ? Task.FromException(new HttpRequestException($"{name} failed"))
                    : release[name].Task;
            }
            mockMastodonClient!.Setup(p => p.UploadMedia(It.IsAny<Stream>())).Returns(async (Stream stream) =>
            {
                mastodonStream = stream;
                await WaitForProvider("mastodon");
                Assert.True(stream.CanRead);
                return new MastodonAttachment() { Id = "attachment" };
            });
            mockBlueskyClient!.Setup(p => p.UploadBlob(It.IsAny<UploadBlobRequest>()))
                .Returns(async (UploadBlobRequest request) =>
                {
                    blueskyStream = request.Content;
                    await WaitForProvider("bluesky");
                    Assert.True(blueskyStream.CanRead);
                    return new UploadBlobResponse()
                    {
                        Blob = new AtProtoBlob() { Type = "blob", Ref = new AtProtoBlobRef() { Link = "blob" }, MimeType = "image/jpeg", Size = 3 }
                    };
                });
            mockThreadsClient!.Setup(p => p.CreateThreadsMediaContainer(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<List<string>>()))
                .Returns(async () =>
                {
                    await WaitForProvider("threads");
                    return "001";
                });

            Task<IActionResult> publishing = controller!.UploadTweet(RequestFor(report));
            try
            {
                await Task.WhenAll(started.Values.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(publishing.IsCompleted);
                Assert.True(mastodonStream!.CanRead);
                Assert.True(blueskyStream!.CanRead);
                mockReportStore.Verify(s => s.ReleaseModerationAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()), Times.Never);
                foreach (var signal in release.Values)
                {
                    signal.TrySetResult();
                }

                Assert.Equal(502, Assert.IsType<ObjectResult>(await publishing.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
                mockReportStore.Verify(s => s.ReleaseModerationAsync(ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
                mockReportedItemsDatabase!.Verify(d => d.SavePublishedReportAsync(It.IsAny<ReportedItem>(),
                    It.IsAny<CancellationToken>()), Times.Never);
                mockFeedProvider!.Verify(p => p.AddReportedItemToFeed(It.IsAny<ReportedItem>()), Times.Never);
                mockReportStore.Verify(s => s.RetireAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()), Times.Never);
                Assert.False(mastodonStream.CanRead);
                Assert.False(blueskyStream.CanRead);
            }
            finally
            {
                foreach (var signal in release.Values)
                {
                    signal.TrySetResult();
                }

                await publishing.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Fact]
        public async Task Publish_ImgurTimeoutReleasesOwnershipAndKeepsPhotosForManualRetry()
        {
            var report = SavedReport(1);
            SetupPublication(report);
            mockImageEndpoint!.Setup(p => p.UploadImageAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<int>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("response lost"));
            var result = Assert.IsType<ObjectResult>(await controller!.UploadTweet(RequestFor(report)));
            Assert.Equal(502, result.StatusCode);
            var failure = Newtonsoft.Json.Linq.JObject.FromObject(result.Value!);
            Assert.True((bool)failure["retryAllowed"]!);
            Assert.Equal(report.Version, (string?)failure["reportVersion"]);
            Assert.Contains("delete any successful posts", (string?)failure["message"]);
            mockReportStore.Verify(s => s.ReleaseModerationAsync(ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
            mockReportStore.Verify(s => s.RetireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Publish_ChangedPhotoReleasesOwnershipBeforeAnyExternalPost()
        {
            var report = SavedReport(1);
            SetupPublication(report);
            SetupJpeg(report.Photos[0]).Setup(b => b.DownloadContentAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(412, "changed"));
            Assert.IsType<ConflictObjectResult>(await controller!.UploadTweet(RequestFor(report)));
            mockReportStore.Verify(s => s.ReleaseModerationAsync(ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
            mockImageEndpoint!.Verify(p => p.UploadImageAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<int>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Delete_UnconfirmedRetirementReturnsFailureAndDoesNotReleaseOrCleanPhotos()
        {
            var report = SavedReport();
            SetupReport(report);
            mockReportStore.Setup(s => s.RetireAsync(ReportId, "operation", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("response lost"));
            Assert.Equal(503, Assert.IsType<ObjectResult>(await controller!.DeletePendingPhoto(RequestFor(report))).StatusCode);
            mockReportStore.Verify(s => s.ReleaseModerationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            mockReportStore.Verify(s => s.CleanupAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData("mastodon")]
        [InlineData("bluesky")]
        [InlineData("threads")]
        [InlineData("database")]
        [InlineData("feed")]
        public async Task Publish_DownstreamFailuresDoNotRetire(string failure)
        {
            var report = SavedReport(1);
            SetupPublication(report);
            if (failure == "mastodon")
            {
                mockMastodonClient!.Setup(p => p.PublishStatus(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<string>()))
                                                    .ThrowsAsync(new HttpRequestException("unavailable"));
            }

            if (failure == "bluesky")
            {
                mockBlueskyClient!.Setup(p => p.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()))
                                                    .ThrowsAsync(new HttpRequestException("unavailable"));
            }

            if (failure == "threads")
            {
                mockThreadsClient!.Setup(p => p.PublishThreadsMediaContainer(It.IsAny<string>()))
                                                    .ThrowsAsync(new HttpRequestException("expired token"));
            }

            if (failure == "database")
            {
                mockReportedItemsDatabase!.Setup(d => d.SavePublishedReportAsync(It.IsAny<ReportedItem>(),
                                                    It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("unavailable"));
            }

            if (failure == "feed")
            {
                mockFeedProvider!.Setup(f => f.AddReportedItemToFeed(It.IsAny<ReportedItem>())).ThrowsAsync(new IOException("unavailable"));
            }

            Assert.Equal(502, Assert.IsType<ObjectResult>(await controller!.UploadTweet(RequestFor(report))).StatusCode);
            mockReportStore.Verify(s => s.RetireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            mockReportStore.Verify(s => s.ReleaseModerationAsync(ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Publish_RetiresOnlyAfterDatabaseAndFeedAndToleratesCleanupFailure()
        {
            var report = SavedReport(1);
            SetupPublication(report);
            bool databaseSaved = false, feedSaved = false;
            mockReportedItemsDatabase!.Setup(d => d.SavePublishedReportAsync(It.IsAny<ReportedItem>(), It.IsAny<CancellationToken>()))
                .Callback(() => databaseSaved = true).Returns(Task.CompletedTask);
            mockFeedProvider!.Setup(f => f.AddReportedItemToFeed(It.IsAny<ReportedItem>()))
                .Callback(() =>
                {
                    Assert.True(databaseSaved);
                    feedSaved = true;
                }).Returns(Task.CompletedTask);
            mockReportStore.Setup(s => s.RetireAsync(ReportId, "operation", It.IsAny<CancellationToken>()))
                .Callback(() => Assert.True(feedSaved)).Returns(Task.CompletedTask);
            mockReportStore.Setup(s => s.CleanupAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("retry cleanup"));
            Assert.IsType<NoContentResult>(await controller!.UploadTweet(RequestFor(report)));
            mockReportStore.Verify(s => s.RetireAsync(ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Publish_CanRetryTheSameItemAfterThreadsFailureIsFixed()
        {
            var report = SavedReport(1);
            SetupPublication(report);
            bool owned = false;
            int attempts = 0;
            mockReportStore.Setup(s => s.BeginModerationAsync(ReportId, "publishing",
                    It.IsAny<IReadOnlyList<FinalizedPhotoUploadMetadata>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, IReadOnlyList<FinalizedPhotoUploadMetadata>? edits, string? _, CancellationToken _) =>
                {
                    Assert.False(owned);
                    owned = true;
                    attempts++;
                    return report with
                    {
                        Photos = [report.Photos[0] with { Metadata = edits![0] }],
                        Moderation = new ModerationOperation("operation", "publishing", DateTimeOffset.UtcNow)
                    };
                });
            mockReportStore.Setup(s => s.ReleaseModerationAsync(ReportId, "operation", It.IsAny<CancellationToken>()))
                .Callback(() => owned = false).Returns(Task.CompletedTask);
            mockThreadsClient!.SetupSequence(p => p.PublishThreadsMediaContainer(It.IsAny<string>()))
                .ThrowsAsync(new HttpRequestException("Threads token expired"))
                .ReturnsAsync("002");

            var request = RequestFor(report);
            var failure = Assert.IsType<ObjectResult>(await controller!.UploadTweet(request));
            Assert.Equal(502, failure.StatusCode);
            Assert.False(owned);
            Assert.Equal(1, attempts);
            mockReportStore.Verify(s => s.RetireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            request.ReportVersion = (string)Newtonsoft.Json.Linq.JObject.FromObject(failure.Value!)["reportVersion"]!;

            // We remove successful posts and fix the token before clicking Upload again.
            Assert.IsType<NoContentResult>(await controller.UploadTweet(request));

            Assert.Equal(2, attempts);
            mockReportStore.Verify(s => s.RetireAsync(ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
            mockReportedItemsDatabase!.Verify(d => d.SavePublishedReportAsync(It.Is<ReportedItem>(item =>
                item.TweetId == ReportId + ".0"), It.IsAny<CancellationToken>()), Times.Once);
            mockBlueskyClient!.Verify(p => p.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()), Times.Exactly(2));
        }

        [Fact]
        public async Task Publish_LostRetirementResponseDoesNotUnlockAnAlreadyCompletedReport()
        {
            var report = SavedReport(1);
            SetupPublication(report);
            mockReportStore.Setup(s => s.RetireAsync(ReportId, "operation", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("lost response"));
            mockReportStore.Setup(s => s.GetAsync(ReportId, report.DeviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(report with
                {
                    Retired = true,
                    Moderation = new ModerationOperation("operation", "publishing", DateTimeOffset.UtcNow)
                });

            Assert.IsType<NoContentResult>(await controller!.UploadTweet(RequestFor(report)));

            mockReportStore.Verify(s => s.ReleaseModerationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Publish_FailureDoesNotOfferRetryIfAnotherRequestAlreadyStarted()
        {
            var report = SavedReport(1);
            SetupReport(report);
            mockReportStore.SetupSequence(s => s.GetForModerationAsync(ReportId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(report)
                .ReturnsAsync(report with
                {
                    Moderation = new ModerationOperation("next-operation", "publishing", DateTimeOffset.UtcNow),
                    Version = "\"next-version\""
                });
            mockMastodonClientProvider!.Setup(p => p.GetServerClient()).Throws(new InvalidOperationException("preflight"));

            var result = Assert.IsType<ObjectResult>(await controller!.UploadTweet(RequestFor(report)));

            Assert.False((bool)Newtonsoft.Json.Linq.JObject.FromObject(result.Value!)["retryAllowed"]!);
            mockReportStore.Verify(s => s.ReleaseModerationAsync(ReportId, "operation", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Publish_RequestOnlyAdminAuthAndUnicodeLocationPreserveAttribution()
        {
            var report = SavedReport(1);
            SetupPublication(report);
            var request = RequestFor(report);
            request.BlueskyAdminDid = "did:plc:admin";
            request.BlueskyAccessJwt = "request-token";
            request.Edits!.PhotoCrossStreet = "Café & Pine 🚲";
            request.Edits.BlueskySubmittedBy = "Submitted by @original.example";
            mockBlueskyClientProvider!.Setup(p => p.GetClient("did:plc:admin", "request-token")).Returns(mockBlueskyClient!.Object);
            mockBlueskyClient.Setup(p => p.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()))
                .Callback<CreateRecordRequest<BskyPost>>(post =>
                {
                    var facet = Assert.Single(post.Record.Facets!);
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(post.Record.Text);
                    Assert.Equal("@original.example", System.Text.Encoding.UTF8.GetString(bytes[(int)facet.Index.ByteStart..(int)facet.Index.ByteEnd]));
                })
                .ReturnsAsync(new CreateRecordResponse() { Cid = "cid", Uri = "at://did:plc:original/app.bsky.feed.post/post" });
            Assert.IsType<NoContentResult>(await controller!.UploadTweet(request));
            mockBlueskyClientProvider.Verify(p => p.GetClient("did:plc:admin", "request-token"), Times.Once);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task Publish_UnchangedGeneratedCreditMentionsOriginalDidWithCurrentHandle(bool canonical, bool handleChanged)
        {
            var report = SavedReport(1);
            var submitted = report.Photos[0].Metadata;
            submitted.BlueskySubmittedBy = $"Submitted by {(canonical ? "@" : "")}{submitted.BlueskyHandle}";
            SetupPublication(report);
            string expectedHandle = handleChanged ? "renamed.example" : submitted.BlueskyHandle!;
            mockBlueskyOAuthProvider!.Setup(p => p.ResolveHandleFromDid(submitted.BlueskyUserDid!, It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedHandle);
            var request = RequestFor(report);
            request.Edits!.BlueskySubmittedBy = submitted.BlueskySubmittedBy;
            mockBlueskyClient!.Setup(p => p.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()))
                .Callback<CreateRecordRequest<BskyPost>>(post =>
                {
                    Assert.Contains($"Submitted by @{expectedHandle}", post.Record.Text);
                    var facet = Assert.Single(post.Record.Facets!);
                    var mention = Assert.IsType<BskyMention>(Assert.Single(facet.Features));
                    Assert.Equal("did:plc:original", mention.Did);
                    if (handleChanged)
                    {
                        Assert.DoesNotContain("@original.example", post.Record.Text);
                    }
                })
                .ReturnsAsync(new CreateRecordResponse() { Cid = "cid", Uri = "at://did:plc:original/app.bsky.feed.post/post" });

            Assert.IsType<NoContentResult>(await controller!.UploadTweet(request));
            Assert.Equal("did:plc:original", report.Receipt.Attribution.BlueskyDid);
        }
    }
}
