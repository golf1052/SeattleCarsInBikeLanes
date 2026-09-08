using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Tests
{
    public class ReportStoreTests
    {
        private const string Id = "0123456789abcdef0123456789abcdef";
        private const string RecordName = ReportStore.BlobPrefix + Id + ".json";
        private static readonly ReportAttribution Anonymous = new ReportAttribution();
        private static readonly string[] InvalidRecordKinds =
        [
            "json", "schema", "oversized", "photos-null", "photo-null", "metadata-null", "photos-empty",
            "receipt-null", "attribution-null", "attribution-invalid", "receipt-submission",
            "attempt-invalid", "attempt-null", "foreign-photo", "foreign-report-photo", "unsafe-photo", "photo-attempt",
            "metadata-report", "metadata-report-missing", "metadata-device", "document-device",
            "etag-null", "etag-empty", "etag-wildcard",
            "moderation-id", "moderation-kind", "moderation-started", "moderation-photo-null",
            "moderation-metadata-null", "moderation-without-owner", "moderation-reference", "moderation-context",
            "started-overflow", "started-utc-overflow", "started-missing", "preparing-with-receipt",
            "abandoned-with-receipt", "accepted-cleanup", "cleanup-null", "cleanup-null-item", "cleanup-late-null-item",
            "cleanup-foreign", "cleanup-unknown-own", "cleanup-unconditional-photo", "cleanup-etag-mismatch",
            "cleanup-incomplete", "cleanup-duplicate", "retired-without-owner", "compacted-with-inventory"
        ];

        public static IEnumerable<object[]> InvalidRecords()
        {
            return InvalidRecordKinds.Select(kind => new object[] { kind });
        }

        public static IEnumerable<object[]> InvalidCleanupRecords()
        {
            return InvalidRecordKinds.SelectMany(kind => new[] { new object[] { kind, true }, new object[] { kind, false } });
        }

        [Theory]
        [InlineData("0123456789abcdef0123456789abcdef", true)]
        [InlineData("ABCDEF0123456789abcdef0123456789", false)]
        [InlineData("0123456789abcdef0123456789abcde", false)]
        [InlineData("../0123456789abcdef0123456789abc", false)]
        [InlineData(null, false)]
        public void RequiresLowercase128BitIds(string? id, bool valid)
        {
            Assert.Equal(valid, ReportStore.IsValidReportId(id));
        }

        [Fact]
        public void KeepsAllPermanentStorageUnderTheEstablishedFinalizedUploadPrefix()
        {
            Assert.Equal("finalizedupload/", ReportStore.FinalizedUploadPrefix);
            Assert.Equal("finalizedupload/reports/", ReportStore.BlobPrefix);
            Assert.Equal("finalizedupload/photos/", ReportStore.PhotoPrefix);
        }

        [Fact]
        public async Task RetiringNewReportLeavesUnadoptedFlatUploadsUntouched()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            blobs.Seed("finalizedupload/existing.jpeg", [1, 2, 3]);
            blobs.Seed("finalizedupload/existing.json", Encoding.UTF8.GetBytes("""{"SubmissionId":"existing"}"""));
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            SubmissionReport owned = await store.BeginModerationAsync(Id, "deleting");

            await store.RetireAsync(Id, owned.Moderation!.Id);
            await store.CleanupAsync();

            Assert.True(blobs.Contains("finalizedupload/existing.jpeg"));
            Assert.True(blobs.Contains("finalizedupload/existing.json"));
            Assert.Empty(blobs.Names(ReportStore.PhotoPrefix));
            Assert.True(blobs.Contains(RecordName));
        }

        [Fact]
        public async Task AcceptsTheWholeSetWithCheckedSeparateJpegsAndImmutableReceipt()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            PreparedSubmissionPhoto[] photos = Prepare(blobs, 4);

            blobs.Before = async operation =>
            {
                if (operation.Kind == "photo")
                {
                    using JsonDocument record = JsonDocument.Parse(blobs.Bytes(RecordName));
                    Assert.Equal("Preparing", record.RootElement.GetProperty("State").GetString());
                    Assert.Equal(4, record.RootElement.GetProperty("Photos").GetArrayLength());
                    Assert.Empty(await Pending(store));
                }
            };

            SubmissionReceipt receipt = await store.CommitAsync(Id, "device", Anonymous, photos);
            SubmissionReport report = Assert.Single(await Pending(store));
            Assert.Equal(receipt, report.Receipt);
            Assert.NotNull(report.Version);
            Assert.Equal(4, report.Photos.Count);
            Assert.Equal("preparation", receipt.SubmissionId);
            Assert.DoesNotContain("\"Version\"", blobs.Text(RecordName));
            for (int index = 0; index < photos.Length; index++)
            {
                SubmissionPhoto photo = report.Photos[index];
                Assert.StartsWith(ReportStore.PhotoPrefix + Id + "/", photo.BlobName);
                Assert.Equal(blobs.Bytes(photos[index].BlobName), blobs.Bytes(photo.BlobName));
                Assert.Equal(blobs.Version(photo.BlobName), photo.ETag);
                Assert.Equal("device", photo.Metadata.DeviceId);
                Assert.Equal(Id, photo.Metadata.ReportId);
                Assert.Contains(blobs.Operations, op => op.Kind == "stream" && op.Name == photos[index].BlobName &&
                    op.Conditions?.IfMatch.ToString() == photos[index].ETag);
                Assert.Contains(blobs.Operations, op => op.Kind == "properties" && op.Name == photo.BlobName &&
                    op.Conditions?.IfMatch.ToString() == photo.ETag);
            }
        }

        [Fact]
        public async Task ReconcilesBeforeValidatingExpiredInputsAndKeepsFirstAttribution()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            ReportAttribution first = new ReportAttribution(BlueskyDid: "did:plc:first");
            SubmissionReceipt accepted = await store.CommitAsync(Id, null, first, Prepare(blobs));
            blobs.Remove("initialupload/photo0.jpeg");

            SubmissionReceipt retry = await store.CommitAsync(Id, null, new ReportAttribution("invalid"), []);

            Assert.Equal(accepted, retry);
            Assert.Equal(first, (await store.GetAsync(Id, null))!.Receipt.Attribution);
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
        }

        [Theory]
        [InlineData(null, "device")]
        [InlineData("device", null)]
        [InlineData("device", "other-device")]
        public async Task DoesNotRebindAnyAcceptedSubmittingContext(string? original, string? replacement)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, original, Anonymous, Prepare(blobs));
            await Assert.ThrowsAsync<ReportDeviceMismatchException>(() => store.GetAsync(Id, replacement));
            await Assert.ThrowsAsync<ReportDeviceMismatchException>(() => store.CommitAsync(Id, replacement, Anonymous, []));
        }

        [Theory]
        [InlineData("Preparing")]
        [InlineData("Accepted")]
        public async Task RecoversLostCoordinatorWriteResponses(string state)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            bool injected = false;
            blobs.After = op =>
            {
                if (!injected && op.State == state)
                {
                    injected = true;
                    throw Unavailable();
                }
                return Task.CompletedTask;
            };
            SubmissionReceipt receipt = await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            Assert.True(injected);
            Assert.Equal(receipt, (await store.GetAsync(Id, null))!.Receipt);
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
        }

        [Fact]
        public async Task UnavailableReconciliationDoesNotUndoAnAcceptedReport()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            int unavailableReads = 0;
            blobs.After = op =>
            {
                if (op.State == "Accepted")
                {
                    unavailableReads = 2;
                    throw Unavailable();
                }
                return Task.CompletedTask;
            };
            blobs.Before = op =>
            {
                if (op.Kind == "read" && unavailableReads-- > 0)
                {
                    throw Unavailable();
                }

                return Task.CompletedTask;
            };
            await Assert.ThrowsAsync<RequestFailedException>(() =>
                store.CommitAsync(Id, null, new ReportAttribution("did:plc:first"), Prepare(blobs)));
            Assert.Contains("\"State\":\"Accepted\"", blobs.Text(RecordName));

            SubmissionReceipt recovered = await store.CommitAsync(Id, null, new ReportAttribution("invalid"), []);
            Assert.Equal("did:plc:first", recovered.Attribution.BlueskyDid);
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
        }

        [Fact]
        public async Task LostReservationWithUnavailableReconciliationRemainsFencedUntilTakeover()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ManualClock clock = new ManualClock();
            ReportStore store = CreateStore(blobs, clock);
            int unavailableReads = 0;
            bool failed = false;
            blobs.After = op =>
            {
                if (!failed && op.State == "Preparing")
                {
                    failed = true;
                    unavailableReads = 1;
                    throw Unavailable();
                }
                return Task.CompletedTask;
            };
            blobs.Before = op => op.Kind == "read" && unavailableReads-- > 0
                ? throw Unavailable() : Task.CompletedTask;
            await Assert.ThrowsAsync<RequestFailedException>(() => store.CommitAsync(Id, null, Anonymous, Prepare(blobs)));
            Assert.Empty(blobs.Names(ReportStore.PhotoPrefix));
            await Assert.ThrowsAsync<ReportInProgressException>(() => store.CommitAsync(Id, null, Anonymous, []));
            clock.Advance(ReportStore.PreparationTimeout);
            SubmissionReceipt receipt = await store.CommitAsync(Id, null, Anonymous, Prepare(blobs, 1, "retry"));
            Assert.Equal("retry", receipt.SubmissionId);
        }

        [Theory]
        [InlineData("record", "Preparing", false)]
        [InlineData("photo", null, false)]
        [InlineData("photo", null, true)]
        [InlineData("record", "Accepted", false)]
        public async Task FailedAttemptCanRetryWithFreshPreparationWithoutLeakingPendingPhotos(
            string kind, string? state, bool after)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            bool failed = false;
            Task Fail(Operation op)
            {
                if (!failed && op.Kind == kind && (state is null || op.State == state))
                {
                    failed = true;
                    throw Unavailable();
                }
                return Task.CompletedTask;
            }
            if (after)
            {
                blobs.After = Fail;
            }
            else
            {
                blobs.Before = Fail;
            }

            await Assert.ThrowsAsync<RequestFailedException>(() => store.CommitAsync(Id, null, Anonymous, Prepare(blobs, 2)));
            Assert.True(failed);
            Assert.Null(await store.GetAsync(Id, null));
            Assert.Empty(await Pending(store));
            string[] old = blobs.Names(ReportStore.PhotoPrefix);
            PreparedSubmissionPhoto[] fresh = Prepare(blobs, 2, "fresh");
            SubmissionReceipt retry = await store.CommitAsync(Id, null, new ReportAttribution("did:plc:retry"), fresh);
            Assert.Equal("fresh", retry.SubmissionId);
            SubmissionReport report = Assert.Single(await Pending(store));
            Assert.DoesNotContain(report.Photos, p => old.Contains(p.BlobName));
            await store.CleanupAsync();
            Assert.Equal(report.Photos.Select(p => p.BlobName).Order(), blobs.Names(ReportStore.PhotoPrefix).Order());
        }

        [Fact]
        public async Task AConcurrentFreshAttemptSeesProgressThenTheOriginalReceipt()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            Gate gate = new Gate();
            blobs.Before = op => op.Kind == "photo" ? gate.Pause() : Task.CompletedTask;
            Task<SubmissionReceipt> first = store.CommitAsync(Id, null, new ReportAttribution("did:plc:first"), Prepare(blobs));
            await gate.Entered.Task;

            ReportInProgressException progress = await Assert.ThrowsAsync<ReportInProgressException>(
                () => store.CommitAsync(Id, null, new ReportAttribution("did:plc:second"), Prepare(blobs, 1, "second")));
            Assert.True(progress.RetryAfter > TimeSpan.Zero);
            await Assert.ThrowsAsync<ReportInProgressException>(() => store.GetAsync(Id, null));
            Assert.Empty(await Pending(store));
            gate.Resume();

            SubmissionReceipt receipt = await first;
            Assert.Equal(receipt, await store.CommitAsync(Id, null, Anonymous, []));
            Assert.Equal("did:plc:first", receipt.Attribution.BlueskyDid);
        }

        [Fact]
        public async Task SimultaneousReservationsUseCreateConditionsAndOneWinner()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            Gate gate = new Gate();
            int prepares = 0;
            blobs.Before = op => op.State == "Preparing" && Interlocked.Increment(ref prepares) == 1
                ? gate.Pause() : Task.CompletedTask;
            Task<SubmissionReceipt> first = store.CommitAsync(Id, null, new ReportAttribution("did:plc:first"), Prepare(blobs));
            await gate.Entered.Task;
            SubmissionReceipt winner = await store.CommitAsync(Id, null, new ReportAttribution("did:plc:second"), Prepare(blobs, 1, "second"));
            gate.Resume();
            Assert.Equal(winner, await first);
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            Assert.All(blobs.Operations.Where(op => op.State == "Preparing"), op =>
                Assert.Equal(ETag.All, op.Conditions!.IfNoneMatch));
        }

        [Fact]
        public async Task TakeoverFencesOldWriterAndUsesDisjointAttemptPaths()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ManualClock clock = new ManualClock();
            ReportStore store = CreateStore(blobs, clock);
            Gate gate = new Gate();
            int uploads = 0;
            blobs.Before = op => op.Kind == "photo" && Interlocked.Increment(ref uploads) == 1
                ? gate.Pause() : Task.CompletedTask;
            Task<SubmissionReceipt> old = store.CommitAsync(Id, null, new ReportAttribution("did:plc:old"), Prepare(blobs));
            await gate.Entered.Task;
            string oldVersion = blobs.Version(RecordName);
            clock.Advance(ReportStore.PreparationTimeout + TimeSpan.FromSeconds(1));

            SubmissionReceipt winner = await store.CommitAsync(Id, null, new ReportAttribution("did:plc:new"), Prepare(blobs, 1, "new"));
            Assert.NotEqual(oldVersion, blobs.Version(RecordName));
            gate.Resume();
            Assert.Equal(winner, await old);
            Assert.Equal(2, blobs.Names(ReportStore.PhotoPrefix).Length);
            Assert.Equal("new", winner.SubmissionId);
            await store.CleanupAsync();
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            Assert.Equal(winner, (await store.GetAsync(Id, null))!.Receipt);
        }

        [Fact]
        public async Task ExpiryCleanupFencesBeforeDeletingAndCollectsRepeatedLateWrites()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ManualClock clock = new ManualClock();
            ReportStore store = CreateStore(blobs, clock);
            Gate gate = new Gate();
            string? delayedPhoto = null;
            blobs.Before = op =>
            {
                if (op.Kind == "photo")
                {
                    delayedPhoto = op.Name;
                    return gate.Pause();
                }
                return Task.CompletedTask;
            };
            Task<SubmissionReceipt> old = store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            await gate.Entered.Task;
            string version = blobs.Version(RecordName);
            clock.Advance(ReportStore.PreparationTimeout + TimeSpan.FromSeconds(1));
            await store.CleanupAsync();
            Assert.NotEqual(version, blobs.Version(RecordName));
            Assert.Contains("\"State\":\"Abandoned\"", blobs.Text(RecordName));
            gate.Resume();
            await Assert.ThrowsAsync<ReportInProgressException>(() => old);
            Assert.True(blobs.Contains(delayedPhoto!));

            await store.CleanupAsync();
            Assert.False(blobs.Contains(delayedPhoto!));
            blobs.Seed(delayedPhoto!, [1, 2, 3]);
            await store.CleanupAsync();
            Assert.False(blobs.Contains(delayedPhoto!));
            Assert.Null(await store.GetAsync(Id, null));
        }

        [Fact]
        public async Task CleanupLosingTheAbandonCasCannotDeleteAcceptedPhotos()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ManualClock clock = new ManualClock();
            ReportStore store = CreateStore(blobs, clock);
            Gate accept = new Gate();
            Gate abandon = new Gate();
            blobs.Before = op => op.State == "Accepted" ? accept.Pause() :
                op.State == "Abandoned" ? abandon.Pause() : Task.CompletedTask;
            Task<SubmissionReceipt> commit = store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            await accept.Entered.Task;
            clock.Advance(ReportStore.PreparationTimeout + TimeSpan.FromSeconds(1));
            Task cleanup = store.CleanupAsync();
            await abandon.Entered.Task;
            accept.Resume();
            SubmissionReceipt receipt = await commit;
            abandon.Resume();
            await cleanup;
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            Assert.Equal(receipt, (await store.GetAsync(Id, null))!.Receipt);
        }

        [Fact]
        public async Task CleanupDoesNotInferSafetyFromMissingRecordsOrUncertainModeration()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ManualClock clock = new ManualClock();
            ReportStore store = CreateStore(blobs, clock);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            await store.BeginModerationAsync(Id, "publishing");
            string orphan = $"{ReportStore.PhotoPrefix}{new string('a', 32)}/{new string('b', 32)}/0.jpeg";
            blobs.Seed(orphan, [8, 9]);
            clock.Advance(TimeSpan.FromDays(900));
            await store.CleanupAsync();
            Assert.Equal(2, blobs.Names(ReportStore.PhotoPrefix).Length);
            Assert.NotNull(Assert.Single(await Pending(store)).Moderation);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task FailedCleanupReadCannotAuthorizeDeletingPhotos(bool photoSweep, bool ioFailure)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            Exception failure = ioFailure ? new IOException("Storage read failed.") : Unavailable();
            int reads = 0;
            blobs.Before = op => op.Kind == "read" && ++reads == (photoSweep ? 2 : 1)
                ? throw failure : Task.CompletedTask;
            Assert.Same(failure, await Record.ExceptionAsync(() => store.CleanupAsync()));
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind == "delete");
        }

        [Theory]
        [MemberData(nameof(InvalidCleanupRecords))]
        public async Task CleanupQuarantinesInvalidRecordsAndTheirPhotosAcrossBothSweeps(
            string corruption, bool recordListed)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            Mock<ILogger<ReportStore>> logger = new Mock<ILogger<ReportStore>>();
            ReportStore store = new ReportStore(blobs.Container.Object, logger.Object);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs, 2));
            string unsafePrefix = ReportStore.PhotoPrefix + Id + "/";
            blobs.Seed($"{unsafePrefix}{new string('b', 32)}/late.jpeg", [8, 9]);
            const string unrelatedPhoto = "initialupload/unrelated.jpeg";
            blobs.Seed(unrelatedPhoto, [5, 6]);
            string unrelatedReportPhoto = $"{ReportStore.PhotoPrefix}{new string('c', 32)}/{new string('d', 32)}/0.jpeg";
            blobs.Seed(unrelatedReportPhoto, [6, 7]);
            Dictionary<string, (byte[] Content, string Version)> unsafePhotos = blobs.Names(unsafePrefix)
                .Append(unrelatedPhoto).Append(unrelatedReportPhoto)
                .ToDictionary(name => name, name => (blobs.Bytes(name), blobs.Version(name)));
            byte[] invalidRecord = InvalidRecordBytes(blobs.Bytes(RecordName), corruption);
            string invalidVersion = blobs.Seed(RecordName, invalidRecord);

            string otherId = new string('a', 32);
            SubmissionReceipt otherReceipt = await store.CommitAsync(otherId, null, Anonymous, Prepare(blobs, 1, "other"));
            string otherPhoto = Assert.Single(blobs.Names(ReportStore.PhotoPrefix + otherId + "/"));
            SubmissionReport owned = await store.BeginModerationAsync(otherId, "deleting");
            blobs.Before = op => op.Kind == "delete" ? throw Unavailable() : Task.CompletedTask;
            await store.RetireAsync(otherId, owned.Moderation!.Id);
            blobs.Before = null;
            string otherOrphan = $"{ReportStore.PhotoPrefix}{otherId}/{new string('b', 32)}/late.jpeg";
            if (!recordListed)
            {
                // A coordinator created after the first listing is only discovered by its photos.
                blobs.Container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None,
                        ReportStore.BlobPrefix, It.IsAny<CancellationToken>()))
                    .Returns(AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(
                        [BlobsModelFactory.BlobItem(name: ReportStore.BlobPrefix + otherId + ".json")],
                        null, Mock.Of<Response>())]));
            }

            for (int sweep = 0; sweep < 2; sweep++)
            {
                blobs.Seed(otherOrphan, [3, 4]);
                blobs.Operations.Clear();

                await store.CleanupAsync();

                Assert.Equal(invalidRecord, blobs.Bytes(RecordName));
                Assert.Equal(invalidVersion, blobs.Version(RecordName));
                Assert.All(unsafePhotos, photo =>
                {
                    Assert.Equal(photo.Value.Content, blobs.Bytes(photo.Key));
                    Assert.Equal(photo.Value.Version, blobs.Version(photo.Key));
                });
                Assert.Single(blobs.Operations, op => op.Kind == "read" && op.Name == RecordName);
                Assert.DoesNotContain(blobs.Operations, op =>
                    op.Kind is "record" or "delete" && (op.Name == RecordName || unsafePhotos.ContainsKey(op.Name)));
                Assert.False(blobs.Contains(otherOrphan));
                Assert.False(blobs.Contains(otherPhoto));
                Assert.Contains("\"Cleanup\":[]", blobs.Text(ReportStore.BlobPrefix + otherId + ".json"));
                Assert.Equal(otherReceipt, (await store.GetAsync(otherId, null))!.Receipt);
            }

            logger.Verify(log => log.Log(LogLevel.Warning, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(Id)),
                It.Is<Exception>(ex => ex is InvalidDataException || ex is JsonException),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(2));
        }

        [Fact]
        public async Task CleanupQuarantinesCorruptionFoundWhileReconcilingAnExpiredReservation()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ManualClock clock = new ManualClock();
            ReportStore store = CreateStore(blobs, clock);
            byte[]? reservation = null;
            blobs.Before = op =>
            {
                if (op.Name == RecordName && op.State == "Preparing")
                {
                    reservation = op.Content;
                }

                return Task.CompletedTask;
            };
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            string photo = Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            blobs.Seed(RecordName, reservation!);
            clock.Advance(ReportStore.PreparationTimeout);

            string otherId = new string('a', 32);
            await store.CommitAsync(otherId, null, Anonymous, Prepare(blobs, 1, "other"));
            string otherOrphan = $"{ReportStore.PhotoPrefix}{otherId}/{new string('b', 32)}/late.jpeg";
            blobs.Seed(otherOrphan, [3, 4]);
            byte[] invalidRecord = InvalidRecordBytes(reservation!, "schema");
            string? invalidVersion = null;
            blobs.Before = op =>
            {
                if (op.Name == RecordName && op.State == "Abandoned")
                {
                    invalidVersion = blobs.Seed(RecordName, invalidRecord);
                    throw Unavailable();
                }
                return Task.CompletedTask;
            };
            blobs.Operations.Clear();

            await store.CleanupAsync();

            Assert.NotNull(invalidVersion);
            Assert.Equal(invalidRecord, blobs.Bytes(RecordName));
            Assert.Equal(invalidVersion, blobs.Version(RecordName));
            Assert.True(blobs.Contains(photo));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind == "delete" && op.Name == photo);
            Assert.Equal(2, blobs.Operations.Count(op => op.Kind == "read" && op.Name == RecordName));
            Assert.False(blobs.Contains(otherOrphan));
        }

        [Theory]
        [InlineData(503, "ServerBusy")]
        [InlineData(403, "AuthorizationFailure")]
        [InlineData(404, "ContainerNotFound")]
        public async Task ReadFailuresNeverBecomeAbsence(int status, string errorCode)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            blobs.Before = op => op.Kind == "read"
                ? throw new RequestFailedException(status, "Failure", errorCode, null) : Task.CompletedTask;
            ReportStore store = CreateStore(blobs);
            await Assert.ThrowsAsync<RequestFailedException>(() => store.GetAsync(Id, null));
            await Assert.ThrowsAsync<RequestFailedException>(() => store.CommitAsync(Id, null, Anonymous, []));
            Assert.Empty(blobs.Names(ReportStore.BlobPrefix));
        }

        [Theory]
        [InlineData(256 * 1024 + 1L)]
        [InlineData(long.MaxValue)]
        public async Task OversizedRecordHeadersAreRejectedBeforeOpeningTheBody(long length)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            byte[] record = blobs.Bytes(RecordName);
            string version = blobs.Version(RecordName);
            string photo = Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            blobs.PropertyLength = name => name == RecordName ? length : null;
            blobs.Operations.Clear();

            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(Id, null));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(Id, null, Anonymous, []));
            await store.CleanupAsync();

            Assert.Equal(record, blobs.Bytes(RecordName));
            Assert.Equal(version, blobs.Version(RecordName));
            Assert.True(blobs.Contains(photo));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind is "record-stream" or "record" or "delete");
        }

        [Fact]
        public async Task RecordReadsPinTheCheckedVersionAndRetryConcurrentChanges()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            SubmissionReceipt receipt = await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            string originalVersion = blobs.Version(RecordName);
            string? replacementVersion = null;
            blobs.After = op =>
            {
                if (op.Kind == "read" && op.Name == RecordName && replacementVersion is null)
                {
                    replacementVersion = blobs.Seed(RecordName, blobs.Bytes(RecordName));
                }

                return Task.CompletedTask;
            };
            blobs.Operations.Clear();

            SubmissionReport report = (await store.GetAsync(Id, null))!;

            Assert.Equal(receipt, report.Receipt);
            Assert.Equal(replacementVersion, report.Version);
            Assert.NotEqual(originalVersion, report.Version);
            Assert.Equal(2, blobs.Operations.Count(op => op.Kind == "read" && op.Name == RecordName));
            Assert.Collection(blobs.Operations.Where(op => op.Kind == "record-stream"),
                op => Assert.Equal(originalVersion, op.Conditions!.IfMatch.ToString()),
                op => Assert.Equal(replacementVersion, op.Conditions!.IfMatch.ToString()));
        }

        [Fact]
        public async Task RepeatedRecordChangesRemainRetryableWithoutAuthorizingDeletion()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            SubmissionReceipt receipt = await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            RequestFailedException failure = new RequestFailedException(412, "Record changed", "ConditionNotMet", null);
            blobs.Before = op => op.Kind == "record-stream" ? throw failure : Task.CompletedTask;
            blobs.Operations.Clear();

            Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() => store.CleanupAsync()));

            Assert.Equal(3, blobs.Operations.Count(op => op.Kind == "read"));
            Assert.Equal(3, blobs.Operations.Count(op => op.Kind == "record-stream"));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind is "record" or "delete");
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            blobs.Before = null;
            Assert.Equal(receipt, (await store.GetAsync(Id, null))!.Receipt);
        }

        [Fact]
        public async Task ARecordRemovedDuringConditionalDownloadCannotAuthorizePhotoDeletion()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            blobs.Before = op =>
            {
                if (op.Kind == "record-stream")
                {
                    blobs.Remove(RecordName);
                }

                return Task.CompletedTask;
            };
            blobs.Operations.Clear();

            Assert.Null(await store.GetAsync(Id, null));
            await store.CleanupAsync();

            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind == "delete");
        }

        [Theory]
        [InlineData(503, "ServerBusy")]
        [InlineData(403, "AuthorizationFailure")]
        [InlineData(404, "ContainerNotFound")]
        [InlineData(412, "LeaseIdMismatchWithBlobOperation")]
        public async Task RecordStreamStorageFailuresAreNotAbsenceOrQuarantine(int status, string errorCode)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            RequestFailedException failure = new RequestFailedException(status, "Read failure", errorCode, null);
            blobs.Before = op => op.Kind == "record-stream" ? throw failure : Task.CompletedTask;
            blobs.Operations.Clear();

            Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() => store.GetAsync(Id, null)));
            Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() => store.CleanupAsync()));

            Assert.Equal(2, blobs.Operations.Count(op => op.Kind == "record-stream"));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind is "record" or "delete");
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
        }

        [Fact]
        public async Task RecordBodyIoFailuresPropagateWithoutAuthorizingDeletion()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            IOException failure = new IOException("Record body interrupted.");
            blobs.RecordStreamFactory = (_, _) => new FailedReadStream(failure);
            blobs.Operations.Clear();

            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => store.GetAsync(Id, null)));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => store.CleanupAsync()));

            Assert.DoesNotContain(blobs.Operations, op => op.Kind is "record" or "delete");
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
        }

        [Theory]
        [InlineData("short")]
        [InlineData("long")]
        [InlineData("endless")]
        [InlineData("oversized-header")]
        public async Task CleanupBoundsInvalidRecordStreamsAndPreservesTheirPhotos(string failure)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            string photo = Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            byte[] record = blobs.Bytes(RecordName);
            string version = blobs.Version(RecordName);
            string otherId = new string('a', 32);
            await store.CommitAsync(otherId, null, Anonymous, Prepare(blobs, 1, "other"));
            string orphan = $"{ReportStore.PhotoPrefix}{otherId}/{new string('b', 32)}/late.jpeg";
            blobs.Seed(orphan, [1, 2]);
            List<ObservedReadStream> streams = [];
            blobs.RecordStreamFactory = (name, bytes) =>
            {
                if (name != RecordName)
                {
                    return new MemoryStream(bytes, writable: false);
                }

                ObservedReadStream stream = failure switch
                {
                    "short" => new ObservedReadStream(bytes[..^1]),
                    "long" => new ObservedReadStream([.. bytes, 255]),
                    "endless" => new ObservedReadStream([], endless: true),
                    _ => new ObservedReadStream(bytes)
                };
                streams.Add(stream);
                return stream;
            };
            if (failure == "endless")
            {
                blobs.PropertyLength = name => name == RecordName ? 256 * 1024 : null;
                blobs.StreamLength = blobs.PropertyLength;
            }
            else if (failure == "oversized-header")
            {
                blobs.StreamLength = name => name == RecordName ? long.MaxValue : null;
            }
            blobs.Operations.Clear();

            await store.CleanupAsync();

            ObservedReadStream observed = Assert.Single(streams);
            Assert.True(observed.IsDisposed);
            Assert.Equal(failure switch
            {
                "short" => record.Length - 1,
                "long" => record.Length + 1,
                "endless" => 256 * 1024 + 1,
                _ => 0
            }, observed.BytesRead);
            Assert.Equal(record, blobs.Bytes(RecordName));
            Assert.Equal(version, blobs.Version(RecordName));
            Assert.True(blobs.Contains(photo));
            Assert.False(blobs.Contains(orphan));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind == "delete" && op.Name == photo);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(Id, null));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(Id, null, Anonymous, []));
            Assert.All(streams, stream => Assert.True(stream.IsDisposed));
        }

        [Theory]
        [MemberData(nameof(InvalidRecords))]
        public async Task CorruptRecordsAreNotTreatedAsAbsent(string corruption)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            byte[] invalidRecord = InvalidRecordBytes(blobs.Bytes(RecordName), corruption);
            string invalidVersion = blobs.Seed(RecordName, invalidRecord);
            blobs.Operations.Clear();

            if (corruption == "json")
            {
                await Assert.ThrowsAnyAsync<JsonException>(() => store.GetAsync(Id, null));
                await Assert.ThrowsAnyAsync<JsonException>(() => store.CommitAsync(Id, null, Anonymous, []));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(Id, null));
                await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(Id, null, Anonymous, []));
            }

            Assert.Equal(invalidRecord, blobs.Bytes(RecordName));
            Assert.Equal(invalidVersion, blobs.Version(RecordName));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind is "record" or "photo" or "delete");
        }

        [Fact]
        public async Task ExistenceRetainsAbandonedAndRetiredCoordinatorRecords()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            Assert.False(await store.ExistsAsync(Id));
            blobs.Before = op => op.Kind == "photo" ? throw Unavailable() : Task.CompletedTask;
            await Assert.ThrowsAsync<RequestFailedException>(() => store.CommitAsync(Id, null, Anonymous, Prepare(blobs)));
            Assert.True(await store.ExistsAsync(Id));
            Assert.Null(await store.GetAsync(Id, null));
            blobs.Before = null;
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            SubmissionReport owned = await store.BeginModerationAsync(Id, "deleting");
            await store.RetireAsync(Id, owned.Moderation!.Id);
            Assert.True(await store.ExistsAsync(Id));
        }

        [Theory]
        [InlineData("empty")]
        [InlineData("count")]
        [InlineData("length")]
        [InlineData("zero")]
        [InlineData("etag")]
        [InlineData("duplicate")]
        [InlineData("order")]
        [InlineData("source")]
        [InlineData("date")]
        [InlineData("latitude")]
        [InlineData("longitude")]
        [InlineData("cars")]
        [InlineData("report")]
        [InlineData("device")]
        [InlineData("metadata-size")]
        public async Task RejectsInvalidBoundsAndMetadataBeforePersisting(string invalid)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            PreparedSubmissionPhoto[] photos = Prepare(blobs);
            switch (invalid)
            {
                case "empty":
                    photos = [];
                    break;
                case "count":
                    photos = Prepare(blobs, 5);
                    break;
                case "length":
                    photos[0] = photos[0] with
                    {
                        Length = ReportStore.MaxPhotoBytes + 1L
                    };
                    break;
                case "zero":
                    photos[0] = photos[0] with
                    {
                        Length = 0
                    };
                    break;
                case "etag":
                    photos[0] = photos[0] with
                    {
                        ETag = "*"
                    };
                    break;
                case "duplicate":
                    photos = [photos[0], photos[0]];
                    break;
                case "order":
                    photos[0].Metadata.PhotoNumber = 1;
                    break;
                case "source":
                    photos[0] = photos[0] with
                    {
                        BlobName = ReportStore.BlobPrefix + "stolen.jpeg"
                    };
                    break;
                case "date":
                    photos[0].Metadata.PhotoDateTime = null;
                    break;
                case "latitude":
                    photos[0].Metadata.PhotoLatitude = "NaN";
                    break;
                case "longitude":
                    photos[0].Metadata.PhotoLongitude = "-180";
                    break;
                case "cars":
                    photos[0].Metadata.NumberOfCars = 0;
                    break;
                case "report":
                    photos[0].Metadata.ReportId = new string('b', 32);
                    break;
                case "device":
                    photos[0].Metadata.DeviceId = "someone";
                    break;
                case "metadata-size":
                    photos[0].Metadata.PhotoCrossStreet = new string('x', 300_000);
                    break;
            }
            await Assert.ThrowsAsync<ArgumentException>(() => CreateStore(blobs).CommitAsync(Id, null, Anonymous, photos));
            Assert.Empty(blobs.Names(ReportStore.BlobPrefix));
            Assert.Empty(blobs.Names(ReportStore.PhotoPrefix));
        }

        [Theory]
        [InlineData(nameof(FinalizedPhotoUploadMetadata.TwitterAccessToken))]
        [InlineData(nameof(FinalizedPhotoUploadMetadata.MastodonAccessToken))]
        [InlineData(nameof(FinalizedPhotoUploadMetadata.ThreadsAccessToken))]
        [InlineData(nameof(FinalizedPhotoUploadMetadata.BlueskyAccessJwt))]
        [InlineData(nameof(FinalizedPhotoUploadMetadata.BlueskyAdminDid))]
        [InlineData(nameof(FinalizedPhotoUploadMetadata.TwitterLink))]
        public async Task RejectsSecretsAndAdminFieldsBeforeAnyRecoveryWrite(string property)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            PreparedSubmissionPhoto[] photos = Prepare(blobs);
            typeof(FinalizedPhotoUploadMetadata).GetProperty(property)!.SetValue(photos[0].Metadata, "secret-value");
            await Assert.ThrowsAsync<ArgumentException>(() => CreateStore(blobs).CommitAsync(Id, null, Anonymous, photos));
            Assert.Empty(blobs.Names(ReportStore.BlobPrefix));
        }

        [Theory]
        [InlineData("changed-version")]
        [InlineData("checked-length")]
        [InlineData("short-stream")]
        [InlineData("long-stream")]
        [InlineData("destination-length")]
        public async Task VerifiesSourceConditionsActualStreamLengthAndDestination(string failure)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            PreparedSubmissionPhoto[] photos = Prepare(blobs);
            switch (failure)
            {
                case "changed-version":
                    blobs.Seed(photos[0].BlobName, [1, 2, 3]);
                    break;
                case "checked-length":
                    photos[0] = photos[0] with
                    {
                        Length = photos[0].Length + 1
                    };
                    break;
                case "short-stream":
                    blobs.StreamBytes = bytes => bytes[..^1];
                    break;
                case "long-stream":
                    blobs.StreamBytes = bytes => [.. bytes, 255];
                    break;
                case "destination-length":
                    blobs.PropertyLength = name => name.StartsWith(ReportStore.PhotoPrefix) ? 999 : null;
                    break;
            }
            ReportStore store = CreateStore(blobs);
            Exception? exception = await Record.ExceptionAsync(() => store.CommitAsync(Id, null, Anonymous, photos));
            if (failure == "destination-length")
            {
                Assert.IsType<InvalidDataException>(exception);
            }
            else
            {
                Assert.IsType<PreparationExpiredException>(exception);
            }

            Assert.Null(await store.GetAsync(Id, null));
            Assert.Empty(await Pending(store));
            await store.CleanupAsync();
            Assert.Empty(blobs.Names(ReportStore.PhotoPrefix));
        }

        [Theory]
        [InlineData(404, "BlobNotFound")]
        [InlineData(412, "ConditionNotMet")]
        public async Task MissingOrChangedSourceHasADedicatedExpiryError(int status, string errorCode)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            blobs.Before = op => op.Kind == "stream"
                ? throw new RequestFailedException(status, "Source changed", errorCode, null) : Task.CompletedTask;
            PreparationExpiredException error = await Assert.ThrowsAsync<PreparationExpiredException>(() =>
                store.CommitAsync(Id, null, Anonymous, Prepare(blobs)));
            Assert.IsAssignableFrom<IOException>(error);
            Assert.Null(await store.GetAsync(Id, null));
        }

        [Fact]
        public async Task SourceExpiryDuringStreamingIsNotConfusedWithDestinationFailure()
        {
            MemoryBlobs blobs = new MemoryBlobs()
            {
                StreamFactory = _ => new FailedReadStream(
                    new RequestFailedException(412, "Source changed during retry", "ConditionNotMet", null))
            };
            await Assert.ThrowsAsync<PreparationExpiredException>(() =>
                CreateStore(blobs).CommitAsync(Id, null, Anonymous, Prepare(blobs)));
        }

        [Theory]
        [InlineData("stream", 404, "ContainerNotFound")]
        [InlineData("stream", 412, "LeaseIdMissing")]
        [InlineData("photo", 404, "ContainerNotFound")]
        [InlineData("photo", 412, "ConditionNotMet")]
        [InlineData("record", 409, "ContainerBeingDeleted")]
        public async Task NonSourceFailuresAreNotReportedAsExpiredPreparation(string kind, int status, string errorCode)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            bool injected = false;
            blobs.Before = op =>
            {
                if (!injected && op.Kind == kind)
                {
                    injected = true;
                    throw new RequestFailedException(status, "Storage failure", errorCode, null);
                }
                return Task.CompletedTask;
            };
            RequestFailedException error = await Assert.ThrowsAsync<RequestFailedException>(() =>
                store.CommitAsync(Id, null, Anonymous, Prepare(blobs)));
            Assert.Equal(errorCode, error.ErrorCode);
        }

        [Theory]
        [InlineData("https://twitter.com/example/status/123")]
        [InlineData("http://twitter.com/example/status/123")]
        public async Task ModerationOwnsTheWholeCanonicalSetAndPreservesSubmittedFactsAndReceipt(string twitterLink)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, new ReportAttribution("did:plc:original"), Prepare(blobs, 2));
            SubmissionReport original = await store.GetForModerationAsync(Id);
            List<FinalizedPhotoUploadMetadata> edits = original.Photos.Select(p => p.Metadata).ToList();
            edits[0].NumberOfCars = 4;
            edits[0].BlueskySubmittedBy = "Edited attribution";
            edits[0].TwitterLink = twitterLink;

            SubmissionReport owned = await store.BeginModerationAsync(Id, "publishing", edits, original.Version);
            Assert.Equal(original.Receipt, owned.Receipt);
            Assert.Equal(original.Photos.Select(p => p.BlobName), owned.Photos.Select(p => p.BlobName));
            Assert.Equal(4, owned.Photos[0].Metadata.NumberOfCars);
            Assert.Equal(twitterLink, owned.Photos[0].Metadata.TwitterLink);
            using JsonDocument stored = JsonDocument.Parse(blobs.Bytes(RecordName));
            Assert.Equal(1, stored.RootElement.GetProperty("Photos")[0].GetProperty("Metadata").GetProperty("NumberOfCars").GetInt32());
            Assert.Equal(JsonValueKind.Null, stored.RootElement.GetProperty("Photos")[0]
                .GetProperty("Metadata").GetProperty("TwitterLink").ValueKind);
            Assert.Equal(twitterLink, stored.RootElement.GetProperty("ModerationPhotos")[0]
                .GetProperty("Metadata").GetProperty("TwitterLink").GetString());
            Assert.NotNull(Assert.Single(await Pending(store)).Moderation);
            await Assert.ThrowsAsync<ReportConflictException>(() => store.BeginModerationAsync(Id, "deleting"));
            await Assert.ThrowsAsync<ReportConflictException>(() => store.ReleaseModerationAsync(Id, "not-owner"));
            await Assert.ThrowsAsync<ReportConflictException>(() => store.RetireAsync(Id, "not-owner"));

            await store.ReleaseModerationAsync(Id, owned.Moderation!.Id);
            Assert.Null((await store.GetForModerationAsync(Id)).Moderation);
            Assert.Equal(1, (await store.GetForModerationAsync(Id)).Photos[0].Metadata.NumberOfCars);
            await Assert.ThrowsAsync<ReportConflictException>(() =>
                store.BeginModerationAsync(Id, "deleting", expectedVersion: original.Version));
        }

        [Fact]
        public async Task SimultaneousPublishAndDeleteCannotBothOwnTheReport()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            Gate gate = new Gate();
            int calls = 0;
            blobs.Before = op => op.ModerationKind is not null && Interlocked.Increment(ref calls) == 1
                ? gate.Pause() : Task.CompletedTask;
            Task<SubmissionReport> publish = store.BeginModerationAsync(Id, "publishing");
            await gate.Entered.Task;
            SubmissionReport deletion = await store.BeginModerationAsync(Id, "deleting");
            gate.Resume();
            await Assert.ThrowsAsync<ReportConflictException>(() => publish);
            Assert.Equal("deleting", deletion.Moderation!.Kind);
            Assert.Equal(deletion.Moderation, (await store.GetForModerationAsync(Id)).Moderation);
        }

        [Fact]
        public async Task RejectsPartialReorderedAndSecretModerationOverrides()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs, 2));
            SubmissionReport report = await store.GetForModerationAsync(Id);
            FinalizedPhotoUploadMetadata[] metadata = report.Photos.Select(p => p.Metadata).ToArray();
            await Assert.ThrowsAsync<ArgumentException>(() => store.BeginModerationAsync(Id, "publishing", [metadata[0]]));
            await Assert.ThrowsAsync<ArgumentException>(() => store.BeginModerationAsync(Id, "publishing", metadata.Reverse().ToArray()));
            metadata[0].MastodonAccessToken = "secret";
            await Assert.ThrowsAsync<ArgumentException>(() => store.BeginModerationAsync(Id, "publishing", metadata));
            Assert.Equal(report.Version, (await store.GetForModerationAsync(Id)).Version);
            Assert.DoesNotContain("secret", blobs.Text(RecordName));
        }

        [Theory]
        [InlineData("date")]
        [InlineData("latitude")]
        [InlineData("longitude")]
        [InlineData("cars")]
        [InlineData("report")]
        [InlineData("device")]
        [InlineData("twitterlink-scheme")]
        [InlineData("twitterlink-relative")]
        [InlineData("twitterlink-credentials")]
        public async Task RejectsInvalidModerationMetadataBeforeTakingOwnership(string invalid)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            SubmissionReport original = await store.GetForModerationAsync(Id);
            FinalizedPhotoUploadMetadata metadata = original.Photos[0].Metadata;
            switch (invalid)
            {
                case "date":
                    metadata.PhotoDateTime = null;
                    break;
                case "latitude":
                    metadata.PhotoLatitude = "NaN";
                    break;
                case "longitude":
                    metadata.PhotoLongitude = "-180";
                    break;
                case "cars":
                    metadata.NumberOfCars = 0;
                    break;
                case "report":
                    metadata.ReportId = new string('b', 32);
                    break;
                case "device":
                    metadata.DeviceId = "another-device";
                    break;
                case "twitterlink-scheme":
                    metadata.TwitterLink = "javascript:alert(1)";
                    break;
                case "twitterlink-relative":
                    metadata.TwitterLink = "/example/status/123";
                    break;
                case "twitterlink-credentials":
                    metadata.TwitterLink = "https://user:secret@twitter.com/example/status/123";
                    break;
            }
            await Assert.ThrowsAsync<ArgumentException>(() => store.BeginModerationAsync(Id, "publishing", [metadata]));
            SubmissionReport unchanged = await store.GetForModerationAsync(Id);
            Assert.Equal(original.Version, unchanged.Version);
            Assert.Null(unchanged.Moderation);
        }

        [Fact]
        public async Task RecoversLostModerationAndRetirementResponses()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            SubmissionReceipt receipt = await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            blobs.After = op => op.Kind == "record" ? throw Unavailable() : Task.CompletedTask;
            SubmissionReport owned = await store.BeginModerationAsync(Id, "deleting");
            await store.RetireAsync(Id, owned.Moderation!.Id);
            await store.RetireAsync(Id, owned.Moderation.Id);
            Assert.Empty(blobs.Names(ReportStore.PhotoPrefix));
            Assert.Empty(await Pending(store));
            Assert.Equal(receipt, await store.CommitAsync(Id, null, Anonymous, []));
            Assert.True((await store.GetAsync(Id, null))!.Retired);
            await Assert.ThrowsAsync<ReportConflictException>(() => store.GetForModerationAsync(Id));
        }

        [Fact]
        public async Task RetirementPersistsInventoryBeforeDeletingAndResumesPartialCleanup()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            SubmissionReceipt receipt = await store.CommitAsync(Id, null, Anonymous, Prepare(blobs, 2));
            SubmissionReport owned = await store.BeginModerationAsync(Id, "publishing");
            int deletions = 0;
            blobs.Before = op =>
            {
                if (op.Kind == "delete")
                {
                    using JsonDocument record = JsonDocument.Parse(blobs.Bytes(RecordName));
                    Assert.Equal("Retired", record.RootElement.GetProperty("State").GetString());
                    Assert.Equal(2, record.RootElement.GetProperty("Cleanup").GetArrayLength());
                    if (Interlocked.Increment(ref deletions) == 2)
                    {
                        throw Unavailable();
                    }
                }
                return Task.CompletedTask;
            };
            await store.RetireAsync(Id, owned.Moderation!.Id);
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            Assert.Empty(await Pending(store));
            Assert.Equal(receipt, (await store.GetAsync(Id, null))!.Receipt);

            await CreateStore(blobs).CleanupAsync();
            Assert.Empty(blobs.Names(ReportStore.PhotoPrefix));
            Assert.Empty((await store.GetAsync(Id, null))!.Photos);
            Assert.Contains("\"Cleanup\":[]", blobs.Text(RecordName));
            Assert.Equal(receipt, await store.CommitAsync(Id, null, new ReportAttribution("changed"), []));
        }

        [Fact]
        public async Task DoesNotDeleteAnythingIfTheRetirementTransitionFailed()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            await store.CommitAsync(Id, null, Anonymous, Prepare(blobs));
            SubmissionReport owned = await store.BeginModerationAsync(Id, "deleting");
            blobs.Before = op => op.State == "Retired" ? throw Unavailable() : Task.CompletedTask;
            await Assert.ThrowsAsync<RequestFailedException>(() => store.RetireAsync(Id, owned.Moderation!.Id));
            Assert.Single(blobs.Names(ReportStore.PhotoPrefix));
            Assert.Single(await Pending(store));
            Assert.DoesNotContain(blobs.Operations, op => op.Kind == "delete");
        }

        [Fact]
        public async Task LegacyAdoptionPreservesReferencesSanitizesCredentialsAndRetiresSidecars()
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            const string legacyId = "old-submission";
            SubmissionPhoto[] photos = Prepare(blobs, 2, legacyId).Select(p =>
            {
                string path = $"finalizedupload/{p.Metadata.PhotoId}.jpeg";
                string etag = blobs.Seed(path, blobs.Bytes(p.BlobName));
                blobs.Seed($"finalizedupload/{p.Metadata.PhotoId}.json", Encoding.UTF8.GetBytes("legacy metadata"));
                p.Metadata.MastodonAccessToken = "old-secret";
                p.Metadata.BlueskyAccessJwt = "old-admin-jwt";
                p.Metadata.DeviceId = "untrusted";
                p.Metadata.PhotoNumber += 10;
                p.Metadata.PhotoDateTime = null;
                p.Metadata.NumberOfCars = null;
                return new SubmissionPhoto(p.Metadata, path, etag, p.Length);
            }).ToArray();
            blobs.After = op => op.State == "Accepted" ? throw Unavailable() : Task.CompletedTask;

            SubmissionReport adopted = await store.AdoptLegacyAsync(legacyId, photos);

            Assert.Equal(legacyId, adopted.Receipt.SubmissionId);
            Assert.Equal(ReportStore.LegacyReportId(legacyId), adopted.Receipt.ReportId);
            Assert.True(ReportStore.IsValidReportId(adopted.Receipt.ReportId));
            Assert.NotEqual(ReportStore.LegacyReportId(legacyId), ReportStore.LegacyReportId("other"));
            Assert.Equal(photos.Select(p => p.BlobName), adopted.Photos.Select(p => p.BlobName));
            Assert.Null(adopted.DeviceId);
            Assert.All(adopted.Photos, p =>
            {
                Assert.Null(p.Metadata.MastodonAccessToken);
                Assert.Null(p.Metadata.BlueskyAccessJwt);
                Assert.Equal("Seattle", p.Metadata.PhotoCrossStreet);
            });
            string adoptedRecord = ReportStore.BlobPrefix + adopted.Receipt.ReportId + ".json";
            Assert.Equal(photos.SelectMany(p => new[]
            {
                p.BlobName, $"finalizedupload/{p.Metadata.PhotoId}.json"
            }).Append(adoptedRecord).Order(), blobs.Names(ReportStore.FinalizedUploadPrefix).Order());
            Assert.Empty(blobs.Names(ReportStore.PhotoPrefix));
            Assert.DoesNotContain("old-secret", blobs.Text(ReportStore.BlobPrefix + adopted.Receipt.ReportId + ".json"));
            Assert.Equal(adopted.Receipt, (await store.AdoptLegacyAsync(legacyId, [])).Receipt);
            SubmissionReport owned = await store.BeginModerationAsync(adopted.Receipt.ReportId, "deleting");
            await store.RetireAsync(adopted.Receipt.ReportId, owned.Moderation!.Id);
            Assert.Equal(adoptedRecord, Assert.Single(blobs.Names(ReportStore.FinalizedUploadPrefix)));
            SubmissionReport retired = await store.AdoptLegacyAsync(legacyId, []);
            Assert.True(retired.Retired);
            Assert.Equal(adopted.Receipt, retired.Receipt);
            Assert.Empty(await Pending(store));
        }

        [Theory]
        [InlineData("legacy-photo-foreign")]
        [InlineData("legacy-sidecars-null")]
        [InlineData("legacy-sidecar-null")]
        [InlineData("legacy-sidecar-foreign")]
        [InlineData("legacy-sidecar-duplicate")]
        [InlineData("legacy-device")]
        [InlineData("legacy-hash")]
        public async Task CorruptLegacyRecordsCannotAuthorizeDeletingPhotosOrSidecars(string corruption)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            const string submissionId = "legacy-corrupt";
            SubmissionPhoto[] photos = Prepare(blobs, 2, submissionId).Select(p =>
            {
                string name = $"finalizedupload/{p.Metadata.PhotoId}.jpeg";
                string etag = blobs.Seed(name, blobs.Bytes(p.BlobName));
                blobs.Seed($"finalizedupload/{p.Metadata.PhotoId}.json", [1, 2]);
                return new SubmissionPhoto(p.Metadata, name, etag, p.Length);
            }).ToArray();
            SubmissionReport adopted = await store.AdoptLegacyAsync(submissionId, photos);
            string id = adopted.Receipt.ReportId;
            string recordName = ReportStore.BlobPrefix + id + ".json";
            byte[] accepted = blobs.Bytes(recordName);
            SubmissionReport owned = await store.BeginModerationAsync(id, "deleting");
            blobs.Before = op => op.Kind == "delete" ? throw Unavailable() : Task.CompletedTask;
            await store.RetireAsync(id, owned.Moderation!.Id);
            blobs.Before = null;
            byte[] retired = blobs.Bytes(recordName);
            const string unrelatedPhoto = "initialupload/unrelated.jpeg";
            blobs.Seed(unrelatedPhoto, [5, 6]);
            Dictionary<string, (byte[] Content, string Version)> retained = blobs.Names("finalizedupload/")
                .Where(name => name != recordName)
                .Append(unrelatedPhoto).ToDictionary(name => name, name => (blobs.Bytes(name), blobs.Version(name)));

            foreach (byte[] record in new[] { accepted, retired })
            {
                byte[] invalid = InvalidRecordBytes(record, corruption);
                string version = blobs.Seed(recordName, invalid);
                blobs.Operations.Clear();

                await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(id, null));
                await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(id, null, Anonymous, []));
                await store.CleanupAsync();

                Assert.Equal(invalid, blobs.Bytes(recordName));
                Assert.Equal(version, blobs.Version(recordName));
                Assert.All(retained, blob =>
                {
                    Assert.Equal(blob.Value.Content, blobs.Bytes(blob.Key));
                    Assert.Equal(blob.Value.Version, blobs.Version(blob.Key));
                });
                Assert.DoesNotContain(blobs.Operations, op => op.Kind is "record" or "delete");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SupportsDottedPhotoIdsWithoutTreatingThemAsPaths(bool legacy)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            ReportStore store = CreateStore(blobs);
            PreparedSubmissionPhoto source = Prepare(blobs)[0];
            source.Metadata.PhotoId = Id + ".0";
            string name = $"{(legacy ? "finalizedupload" : "initialupload")}/{source.Metadata.PhotoId}.jpeg";
            string etag = blobs.Seed(name, blobs.Bytes(source.BlobName));
            if (legacy)
            {
                SubmissionReport report = await store.AdoptLegacyAsync(source.Metadata.SubmissionId,
                                                    [new SubmissionPhoto(source.Metadata, name, etag, source.Length)]);
                Assert.Equal(name, Assert.Single(report.Photos).BlobName);
            }
            else
            {
                await store.CommitAsync(Id, null, Anonymous, [source with { BlobName = name, ETag = etag }]);
                Assert.Equal(Id + ".0", Assert.Single((await store.GetAsync(Id, null))!.Photos).Metadata.PhotoId);
            }
        }

        [Theory]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("../photo")]
        [InlineData("parent/photo")]
        [InlineData("parent\\photo")]
        public async Task RejectsPathComponentsInPhotoIds(string photoId)
        {
            MemoryBlobs blobs = new MemoryBlobs();
            PreparedSubmissionPhoto[] photos = Prepare(blobs);
            photos[0].Metadata.PhotoId = photoId;
            await Assert.ThrowsAsync<ArgumentException>(() => CreateStore(blobs).CommitAsync(Id, null, Anonymous, photos));
            Assert.Empty(blobs.Names(ReportStore.BlobPrefix));
        }

        private static ReportStore CreateStore(MemoryBlobs blobs, TimeProvider? clock = null)
        {
            return new ReportStore(blobs.Container.Object, NullLogger<ReportStore>.Instance, clock);
        }

        private static byte[] InvalidRecordBytes(byte[] record, string corruption)
        {
            if (corruption == "json")
            {
                return Encoding.UTF8.GetBytes("{broken");
            }

            if (corruption == "oversized")
            {
                return new byte[256 * 1024 + 1];
            }

            JsonNode document = JsonNode.Parse(record)!;
            JsonArray photos = document["Photos"]!.AsArray();
            JsonNode photo = photos[0]!;
            JsonNode metadata = photo["Metadata"]!;
            if (corruption.StartsWith("moderation-", StringComparison.Ordinal) ||
                corruption.StartsWith("cleanup-", StringComparison.Ordinal) ||
                corruption is "retired-without-owner" or "compacted-with-inventory")
            {
                document["Moderation"] = new JsonObject()
                {
                    ["Id"] = new string('b', 32),
                    ["Kind"] = "deleting",
                    ["StartedAt"] = "2026-09-07T12:00:00+00:00"
                };
            }
            if (corruption.StartsWith("moderation-", StringComparison.Ordinal))
            {
                document["ModerationPhotos"] = photos.DeepClone();
            }

            if (corruption.StartsWith("cleanup-", StringComparison.Ordinal) ||
                corruption is "retired-without-owner" or "compacted-with-inventory")
            {
                document["State"] = "Retired";
                document["Cleanup"] = new JsonArray(photos.Select(p => (JsonNode)new JsonObject()
                {
                    ["BlobName"] = p!["BlobName"]!.GetValue<string>(),
                    ["ETag"] = p["ETag"]!.GetValue<string>()
                }).ToArray());
            }

            switch (corruption)
            {
                case "schema":
                    document["SchemaVersion"] = 2;
                    break;
                case "photos-null":
                    document["Photos"] = null;
                    break;
                case "photo-null":
                    photos[0] = null;
                    break;
                case "metadata-null":
                    photo["Metadata"] = null;
                    break;
                case "photos-empty":
                    document["Photos"] = new JsonArray();
                    break;
                case "receipt-null":
                    document["Receipt"] = null;
                    break;
                case "attribution-null":
                    document["Receipt"]!["Attribution"] = null;
                    break;
                case "attribution-invalid":
                    document["Receipt"]!["Attribution"]!["BlueskyDid"] = "invalid";
                    break;
                case "receipt-submission":
                    document["Receipt"]!["SubmissionId"] = "different-submission";
                    break;
                case "attempt-invalid":
                    document["AttemptId"] = "../invalid";
                    break;
                case "attempt-null":
                    document["AttemptId"] = null;
                    break;
                case "foreign-photo":
                    photo["BlobName"] = "initialupload/unrelated.jpeg";
                    break;
                case "foreign-report-photo":
                    photo["BlobName"] = $"{ReportStore.PhotoPrefix}{new string('c', 32)}/{new string('d', 32)}/0.jpeg";
                    break;
                case "unsafe-photo":
                    photo["BlobName"] = photo["BlobName"]!.GetValue<string>() + "/../other.jpeg";
                    break;
                case "photo-attempt":
                    photo["BlobName"] = $"{ReportStore.PhotoPrefix}{Id}/{new string('c', 32)}/0.jpeg";
                    break;
                case "metadata-report":
                    metadata["ReportId"] = new string('c', 32);
                    break;
                case "metadata-report-missing":
                    metadata["ReportId"] = null;
                    break;
                case "metadata-device":
                    metadata["DeviceId"] = "other-device";
                    break;
                case "document-device":
                    document["DeviceId"] = "device";
                    break;
                case "etag-null":
                    photo["ETag"] = null;
                    break;
                case "etag-empty":
                    photo["ETag"] = "";
                    break;
                case "etag-wildcard":
                    photo["ETag"] = "*";
                    break;
                case "moderation-id":
                    document["Moderation"]!["Id"] = "invalid";
                    break;
                case "moderation-kind":
                    document["Moderation"]!["Kind"] = "invalid";
                    break;
                case "moderation-started":
                    document["Moderation"]!["StartedAt"] = DateTimeOffset.MaxValue;
                    break;
                case "moderation-photo-null":
                    document["ModerationPhotos"]![0] = null;
                    break;
                case "moderation-metadata-null":
                    document["ModerationPhotos"]![0]!["Metadata"] = null;
                    break;
                case "moderation-without-owner":
                    document["Moderation"] = null;
                    break;
                case "moderation-reference":
                    document["ModerationPhotos"]![0]!["ETag"] = "\"other-version\"";
                    break;
                case "moderation-context":
                    document["ModerationPhotos"]![0]!["Metadata"]!["DeviceId"] = "other";
                    break;
                case "started-overflow":
                    document["StartedAt"] = DateTimeOffset.MaxValue;
                    break;
                case "started-utc-overflow":
                    document["StartedAt"] = "9999-12-31T22:59:59-01:00";
                    break;
                case "started-missing":
                    document.AsObject().Remove("StartedAt");
                    break;
                case "preparing-with-receipt":
                    document["State"] = "Preparing";
                    break;
                case "abandoned-with-receipt":
                    document["State"] = "Abandoned";
                    break;
                case "accepted-cleanup":
                    document["Cleanup"] = new JsonArray();
                    break;
                case "cleanup-null":
                    document["Cleanup"] = null;
                    break;
                case "cleanup-null-item":
                    document["Cleanup"]![0] = null;
                    break;
                case "cleanup-late-null-item":
                    document["Cleanup"]!.AsArray()[photos.Count - 1] = null;
                    break;
                case "cleanup-foreign":
                    document["Cleanup"]![0]!["BlobName"] = "initialupload/unrelated.jpeg";
                    document["Cleanup"]![0]!["ETag"] = null;
                    break;
                case "cleanup-unknown-own":
                    document["Cleanup"]![0]!["BlobName"] = $"{ReportStore.PhotoPrefix}{Id}/{new string('b', 32)}/late.jpeg";
                    document["Cleanup"]![0]!["ETag"] = null;
                    break;
                case "cleanup-unconditional-photo":
                    document["Cleanup"]![0]!["ETag"] = null;
                    break;
                case "cleanup-etag-mismatch":
                    document["Cleanup"]![0]!["ETag"] = "\"other-version\"";
                    break;
                case "cleanup-incomplete":
                    document["Cleanup"]!.AsArray().RemoveAt(0);
                    break;
                case "cleanup-duplicate":
                    document["Cleanup"]!.AsArray().Add(document["Cleanup"]![0]!.DeepClone());
                    break;
                case "retired-without-owner":
                    document["Moderation"] = null;
                    break;
                case "compacted-with-inventory":
                    document["Photos"] = new JsonArray();
                    break;
                case "legacy-photo-foreign":
                    photo["BlobName"] = "initialupload/unrelated.jpeg";
                    if (document["Cleanup"] is { } photoCleanup)
                    {
                        photoCleanup[0]!["BlobName"] = "initialupload/unrelated.jpeg";
                    }

                    break;
                case "legacy-sidecars-null":
                    document["LegacySidecars"] = null;
                    break;
                case "legacy-sidecar-null":
                    document["LegacySidecars"]![0] = null;
                    break;
                case "legacy-sidecar-foreign":
                    document["LegacySidecars"]![0] = "initialupload/unrelated.jpeg";
                    if (document["Cleanup"] is { } sidecarCleanup)
                    {
                        sidecarCleanup[photos.Count]!["BlobName"] = "initialupload/unrelated.jpeg";
                    }

                    break;
                case "legacy-sidecar-duplicate":
                    document["LegacySidecars"]![1] = document["LegacySidecars"]![0]!.GetValue<string>();
                    break;
                case "legacy-device":
                    document["DeviceId"] = "device";
                    foreach (JsonNode? p in photos)
                    {
                        p!["Metadata"]!["DeviceId"] = "device";
                    }

                    break;
                case "legacy-hash":
                    document["Receipt"]!["SubmissionId"] = "different-legacy-submission";
                    foreach (JsonNode? p in photos)
                    {
                        p!["Metadata"]!["SubmissionId"] = "different-legacy-submission";
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption));
            }
            return Encoding.UTF8.GetBytes(document.ToJsonString());
        }

        private static PreparedSubmissionPhoto[] Prepare(MemoryBlobs blobs, int count = 1, string submissionId = "preparation")
        {
            return Enumerable.Range(0, count).Select(index =>
                                    {
                                        string photoId = submissionId == "preparation" ? $"photo{index}" : $"{submissionId}-photo{index}";
                                        string name = $"initialupload/{photoId}.jpeg";
                                        byte[] bytes = [0xff, 0xd8, (byte)index, 0xff, 0xd9];
                                        string etag = blobs.Seed(name, bytes);
                                        return new PreparedSubmissionPhoto(new FinalizedPhotoUploadMetadata()
                                        {
                                            PhotoId = photoId,
                                            SubmissionId = submissionId,
                                            PhotoNumber = index,
                                            PhotoDateTime = new DateTime(2026, 9, 7),
                                            PhotoLatitude = "47.6",
                                            PhotoLongitude = "-122.3",
                                            PhotoCrossStreet = "Seattle",
                                            NumberOfCars = 1,
                                            Tags = []
                                        }, name, etag, bytes.Length);
                                    }).ToArray();
        }

        private static async Task<List<SubmissionReport>> Pending(ReportStore store)
        {
            List<SubmissionReport> result = [];
            await foreach (SubmissionReport report in store.GetPendingAsync())
            {
                result.Add(report);
            }

            return result;
        }

        private static RequestFailedException Unavailable()
        {
            return new RequestFailedException(503, "Storage unavailable", "ServerBusy", null);
        }

        private sealed class ManualClock : TimeProvider
        {
            private DateTimeOffset now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

            public override DateTimeOffset GetUtcNow()
            {
                return now;
            }

            public void Advance(TimeSpan duration)
            {
                now += duration;
            }
        }

        private sealed class Gate
        {
            public TaskCompletionSource Entered { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task Pause()
            {
                Entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }

            public void Resume()
            {
                release.TrySetResult();
            }
        }

        private sealed class FailedReadStream : MemoryStream
        {
            private readonly Exception failure;

            public FailedReadStream(Exception failure) : base(new byte[] { 1 })
            {
                this.failure = failure;
            }

            public override int Read(Span<byte> buffer)
            {
                throw failure;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromException<int>(failure);
            }
        }

        private sealed class ObservedReadStream : MemoryStream
        {
            private readonly bool endless;

            public ObservedReadStream(byte[] bytes, bool endless = false) : base(bytes, writable: false)
            {
                this.endless = endless;
            }

            public int BytesRead { get; private set; }
            public bool IsDisposed { get; private set; }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count;
                if (endless)
                {
                    buffer.Span.Fill((byte)' ');
                    count = buffer.Length;
                }
                else
                {
                    count = base.Read(buffer.Span);
                }
                BytesRead += count;
                return ValueTask.FromResult(count);
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed record Operation(string Kind, string Name, BlobRequestConditions? Conditions = null, byte[]? Content = null)
        {
            public string? State => Property("State");
            public string? ModerationKind => Property("Moderation", "Kind");
            private string? Property(string name, string? child = null)
            {
                if (Kind != "record" || Content is null)
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(Content);
                JsonElement value = document.RootElement.GetProperty(name);
                return value.ValueKind == JsonValueKind.Null ? null :
                    child is null ? value.GetString() : value.GetProperty(child).GetString();
            }
        }

        /// <summary>A distinct client and versioned byte stream per path, with atomic Azure request conditions.</summary>
        private sealed class MemoryBlobs
        {
            private sealed record Stored(byte[] Bytes, string ETag);

            private readonly Dictionary<string, Stored> storage = new Dictionary<string, Stored>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, Mock<BlobClient>> clients = new ConcurrentDictionary<string, Mock<BlobClient>>(StringComparer.Ordinal);
            private readonly object mutex = new object();
            private int version;
            public Mock<BlobContainerClient> Container { get; } = new Mock<BlobContainerClient>(MockBehavior.Strict);
            public ConcurrentQueue<Operation> Operations { get; } = new ConcurrentQueue<Operation>();
            public Func<Operation, Task>? Before { get; set; }
            public Func<Operation, Task>? After { get; set; }
            public Func<byte[], byte[]>? StreamBytes { get; set; }
            public Func<byte[], Stream>? StreamFactory { get; set; }
            public Func<string, byte[], Stream>? RecordStreamFactory { get; set; }
            public Func<string, long?>? PropertyLength { get; set; }
            public Func<string, long?>? StreamLength { get; set; }

            public MemoryBlobs()
            {
                Container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
                                                    .Returns((string name) => clients.GetOrAdd(name, CreateBlob).Object);
                Container.Setup(c => c.GetBlobsAsync(It.IsAny<BlobTraits>(), It.IsAny<BlobStates>(),
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .Returns((BlobTraits _, BlobStates _, string prefix, CancellationToken _) =>
                    {
                        BlobItem[] items;
                        lock (mutex)
                        {
                            items = storage.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                                                                                        .Select(pair => BlobsModelFactory.BlobItem(name: pair.Key,
                                                                                            properties: BlobsModelFactory.BlobItemProperties(
                                                                                                accessTierInferred: false, blobSequenceNumber: null, blobType: BlobType.Block,
                                                                                                contentLength: pair.Value.Bytes.Length, eTag: new ETag(pair.Value.ETag),
                                                                                                lastModified: DateTimeOffset.UtcNow))).ToArray();
                        }
                        return AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(items, null, Mock.Of<Response>())]);
                    });
            }

            public string Seed(string name, byte[] bytes)
            {
                lock (mutex)
                {
                    string etag = $"\"{++version}\"";
                    storage[name] = new Stored(bytes.ToArray(), etag);
                    return etag;
                }
            }

            public bool Contains(string name)
            {
                lock (mutex)
                {
                    return storage.ContainsKey(name);
                }
            }

            public void Remove(string name)
            {
                lock (mutex)
                {
                    storage.Remove(name);
                }
            }

            public byte[] Bytes(string name)
            {
                lock (mutex)
                {
                    return storage[name].Bytes.ToArray();
                }
            }

            public string Text(string name)
            {
                return Encoding.UTF8.GetString(Bytes(name));
            }

            public string Version(string name)
            {
                lock (mutex)
                {
                    return storage[name].ETag;
                }
            }

            public string[] Names(string prefix)
            {
                lock (mutex)
                {
                    return storage.Keys.Where(k => k.StartsWith(prefix)).ToArray();
                }
            }

            private async Task Start(Operation operation)
            {
                Operations.Enqueue(operation);
                if (Before is not null)
                {
                    await Before(operation);
                }
            }

            private async Task End(Operation operation)
            {
                if (After is not null)
                {
                    await After(operation);
                }
            }

            private Stored Read(string name, BlobRequestConditions? conditions = null)
            {
                lock (mutex)
                {
                    Check(name, conditions);
                    return storage.TryGetValue(name, out Stored? value)
                        ? value : throw new RequestFailedException(404, "Missing blob", "BlobNotFound", null);
                }
            }

            private void Check(string name, BlobRequestConditions? conditions)
            {
                bool exists = storage.TryGetValue(name, out Stored? current);
                if (conditions?.IfNoneMatch == ETag.All && exists ||
                    conditions?.IfMatch is ETag match && (!exists || current!.ETag != match.ToString()))
                {
                    throw new RequestFailedException(412, "ETag condition failed", "ConditionNotMet", null);
                }
            }

            private Mock<BlobClient> CreateBlob(string name)
            {
                Mock<BlobClient> blob = new Mock<BlobClient>(MockBehavior.Strict);
                blob.SetupGet(c => c.Name).Returns(name);
                blob.Setup(c => c.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
                    .Returns(async (BlobDownloadOptions options, CancellationToken _) =>
                    {
                        bool record = name.StartsWith(ReportStore.BlobPrefix, StringComparison.Ordinal);
                        Operation op = new Operation(record ? "record-stream" : "stream", name, options.Conditions);
                        await Start(op);
                        Stored current = Read(name, options.Conditions);
                        Stream stream = record
                            ? RecordStreamFactory?.Invoke(name, current.Bytes) ?? new MemoryStream(current.Bytes, writable: false)
                            : StreamFactory?.Invoke(current.Bytes) ??
                                new MemoryStream(StreamBytes?.Invoke(current.Bytes) ?? current.Bytes, writable: false);
                        BlobDownloadStreamingResult result = BlobsModelFactory.BlobDownloadStreamingResult(
                            stream, Details(current, StreamLength?.Invoke(name)));
                        await End(op);
                        return Response.FromValue(result, Mock.Of<Response>());
                    });
                blob.Setup(c => c.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                    .Returns(async (BlobRequestConditions? conditions, CancellationToken _) =>
                    {
                        Operation op = new Operation(name.StartsWith(ReportStore.BlobPrefix, StringComparison.Ordinal)
                                                                            ? "read" : "properties", name, conditions);
                        await Start(op);
                        Stored current = Read(name, conditions);
                        BlobProperties properties = BlobsModelFactory.BlobProperties(
                            contentLength: PropertyLength?.Invoke(name) ?? current.Bytes.Length,
                            eTag: new ETag(current.ETag));
                        await End(op);
                        return Response.FromValue(properties, Mock.Of<Response>());
                    });
                blob.Setup(c => c.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
                    .Returns(async (BinaryData data, BlobUploadOptions options, CancellationToken _) =>
                    {
                        Operation op = new Operation("record", name, options.Conditions, data.ToArray());
                        await Start(op);
                        string etag;
                        lock (mutex)
                        {
                            Check(name, options.Conditions);
                            etag = Seed(name, data.ToArray());
                        }
                        await End(op);
                        return Uploaded(etag);
                    });
                blob.Setup(c => c.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
                    .Returns(async (Stream stream, BlobUploadOptions options, CancellationToken token) =>
                    {
                        Operation op = new Operation("photo", name, options.Conditions);
                        await Start(op);
                        Assert.Equal("image/jpeg", options.HttpHeaders.ContentType);
                        Assert.Equal(ETag.All, options.Conditions.IfNoneMatch);
                        Assert.Equal(StorageChecksumAlgorithm.Auto, options.TransferValidation.ChecksumAlgorithm);
                        Assert.Equal(1, options.TransferOptions.MaximumConcurrency);
                        using MemoryStream copy = new MemoryStream();
                        await stream.CopyToAsync(copy, token);
                        string etag;
                        lock (mutex)
                        {
                            Check(name, options.Conditions);
                            etag = Seed(name, copy.ToArray());
                        }
                        await End(op);
                        return Uploaded(etag);
                    });
                blob.Setup(c => c.DeleteIfExistsAsync(It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(),
                        It.IsAny<CancellationToken>()))
                    .Returns(async (DeleteSnapshotsOption _, BlobRequestConditions? conditions, CancellationToken _) =>
                    {
                        Operation op = new Operation("delete", name, conditions);
                        await Start(op);
                        bool deleted;
                        lock (mutex)
                        {
                            if (!storage.ContainsKey(name))
                            {
                                return Response.FromValue(false, Mock.Of<Response>());
                            }

                            Check(name, conditions);
                            deleted = storage.Remove(name);
                        }
                        await End(op);
                        return Response.FromValue(deleted, Mock.Of<Response>());
                    });
                return blob;
            }

            private static BlobDownloadDetails Details(Stored blob, long? length = null)
            {
                return BlobsModelFactory.BlobDownloadDetails(
                                                contentLength: length ?? blob.Bytes.Length, eTag: new ETag(blob.ETag));
            }

            private static Response<BlobContentInfo> Uploaded(string etag)
            {
                return Response.FromValue(
                                                BlobsModelFactory.BlobContentInfo(eTag: new ETag(etag), lastModified: DateTimeOffset.UtcNow,
                                                    contentHash: null, versionId: null, encryptionKeySha256: null, encryptionScope: null, blobSequenceNumber: 0),
                                                Mock.Of<Response>());
            }
        }
    }
}
