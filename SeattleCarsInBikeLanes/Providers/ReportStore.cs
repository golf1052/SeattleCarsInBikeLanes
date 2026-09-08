using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Providers
{
    public sealed record PreparedSubmissionPhoto(FinalizedPhotoUploadMetadata Metadata, string BlobName, string ETag, long Length);

    public sealed record SubmissionPhoto(FinalizedPhotoUploadMetadata Metadata, string BlobName, string ETag, long Length);

    public sealed record ModerationOperation(string Id, string Kind, DateTimeOffset StartedAt);

    public sealed record SubmissionReport(SubmissionReceipt Receipt, string? DeviceId, List<SubmissionPhoto> Photos,
        bool Retired = false, ModerationOperation? Moderation = null)
    {
        [JsonIgnore]
        public string? Version { get; init; }
    }

    public sealed class ReportInProgressException : Exception
    {
        public TimeSpan RetryAfter { get; }

        public ReportInProgressException(TimeSpan retryAfter) : base("This report is still being prepared.")
        {
            RetryAfter = retryAfter;
        }
    }

    public sealed class ReportConflictException : Exception
    {
        public ReportConflictException(string message) : base(message)
        {
        }
    }

    public sealed class ReportDeviceMismatchException : UnauthorizedAccessException
    {
        public ReportDeviceMismatchException() : base("This report belongs to a different submitting context.")
        {
        }
    }

    public sealed class PreparationExpiredException : IOException
    {
        public PreparationExpiredException(string message = "The prepared photo expired or changed.",
            Exception? innerException = null) : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// The report blob is the sole acceptance and moderation coordinator. Its ETag fences every
    /// attempt; photo blobs are immutable and are never authoritative on their own.
    /// </summary>
    public class ReportStore
    {
        public const string FinalizedUploadPrefix = "finalizedupload/";
        public const string BlobPrefix = FinalizedUploadPrefix + "reports/";
        public const string PhotoPrefix = FinalizedUploadPrefix + "photos/";
        public const int MaxPhotoBytes = 8 * 1024 * 1024;
        public const int MaxReportBytes = 4 * MaxPhotoBytes;
        public static readonly TimeSpan PreparationTimeout = TimeSpan.FromMinutes(15);
        private const int MaxRecordBytes = 256 * 1024;
        private readonly BlobContainerClient container;
        private readonly ILogger<ReportStore> logger;
        private readonly TimeProvider clock;

        public ReportStore(BlobContainerClient container, ILogger<ReportStore> logger, TimeProvider? clock = null)
        {
            this.container = container;
            this.logger = logger;
            this.clock = clock ?? TimeProvider.System;
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        private enum ReportState
        {
            Preparing,
            Accepted,
            Abandoned,
            Retired
        }

        private sealed record CleanupItem(string BlobName, string? ETag);

        private sealed record Document(
            string ReportId,
            string? DeviceId,
            ReportState State,
            DateTimeOffset StartedAt,
            string? AttemptId,
            List<SubmissionPhoto> Photos,
            SubmissionReceipt? Receipt = null,
            ModerationOperation? Moderation = null,
            List<SubmissionPhoto>? ModerationPhotos = null,
            List<string>? LegacySidecars = null,
            List<CleanupItem>? Cleanup = null,
            int SchemaVersion = 1,
            string? MutationId = null)
        {
            [JsonIgnore]
            public string? Version { get; init; }
        }

        public static bool IsValidReportId(string? id)
        {
            return id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        }

        public static string LegacyReportId(string legacySubmissionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(legacySubmissionId);
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
                "SeattleCarsInBikeLanes:legacy-submission:v1\0" + legacySubmissionId));
            return Convert.ToHexStringLower(digest.AsSpan(0, 16));
        }

        public virtual async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
        {
            return await ReadAsync(id, cancellationToken) is not null;
        }

        public virtual async Task<SubmissionReport?> GetAsync(string id, string? deviceId, CancellationToken cancellationToken = default)
        {
            Document? document = await ReadAsync(id, cancellationToken);
            if (document is null)
            {
                return null;
            }
            CheckDevice(document, deviceId);
            return document.State switch
            {
                ReportState.Accepted or ReportState.Retired => ToReport(document),
                ReportState.Abandoned => null,
                _ => throw InProgress(document)
            };
        }

        public virtual async Task<SubmissionReceipt> CommitAsync(string id, string? deviceId, ReportAttribution attribution,
            IReadOnlyList<PreparedSubmissionPhoto> photos, CancellationToken cancellationToken = default)
        {
            Document? current = await ReadAsync(id, cancellationToken);
            if (current is not null)
            {
                CheckDevice(current, deviceId);
                if (IsAccepted(current))
                {
                    return current.Receipt!;
                }
                if (IsLive(current))
                {
                    throw InProgress(current);
                }
            }

            ValidateAttribution(attribution);
            ArgumentNullException.ThrowIfNull(photos);
            photos = photos.ToArray();
            ValidatePhotos(photos.Select(p => new SubmissionPhoto(p.Metadata, p.BlobName, p.ETag, p.Length)).ToList(),
                id, deviceId, legacy: false);
            foreach (PreparedSubmissionPhoto photo in photos)
            {
                if (!IsPhotoPath(photo.BlobName, "initialupload/"))
                {
                    throw new ArgumentException("Prepared photos must refer to initial-upload JPEGs.", nameof(photos));
                }
            }

            string attemptId = Guid.NewGuid().ToString("N");
            List<SubmissionPhoto> reserved = photos.Select((photo, index) => new SubmissionPhoto(
                CloneMetadata(photo.Metadata, id, deviceId),
                $"{PhotoPrefix}{id}/{attemptId}/{index}.jpeg", "", photo.Length)).ToList();
            CheckMetadataSize(reserved);
            Document preparing;
            while (true)
            {
                if (current is not null)
                {
                    CheckDevice(current, deviceId);
                    if (IsAccepted(current))
                    {
                        return current.Receipt!;
                    }
                    if (IsLive(current))
                    {
                        throw InProgress(current);
                    }
                }

                preparing = new Document(id, deviceId, ReportState.Preparing, clock.GetUtcNow(), attemptId, reserved);
                try
                {
                    // Reserving the complete destination set precedes even the first photo write.
                    preparing = await WriteAsync(preparing, current?.Version, cancellationToken);
                    break;
                }
                catch (RequestFailedException ex) when (IsConditionFailure(ex))
                {
                    current = await ReadAsync(id, cancellationToken);
                }
            }

            try
            {
                List<SubmissionPhoto> completed = new List<SubmissionPhoto>();
                for (int index = 0; index < photos.Count; index++)
                {
                    SubmissionPhoto stored = await CopyPhotoAsync(photos[index], reserved[index], cancellationToken);
                    completed.Add(stored);
                }

                SubmissionReceipt receipt = new SubmissionReceipt(id, reserved[0].Metadata.SubmissionId, clock.GetUtcNow(), attribution);
                try
                {
                    await WriteAsync(preparing with
                    {
                        State = ReportState.Accepted,
                        Photos = completed,
                        Receipt = receipt
                    },
                                                                preparing.Version, cancellationToken);
                }
                catch (RequestFailedException ex) when (IsConditionFailure(ex))
                {
                    throw new ReportInProgressException(TimeSpan.FromSeconds(1));
                }
                return receipt;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // An upload/accept response may be lost after it committed. Never abandon on the
                // strength of that response alone, or validate fresh input before reconciling.
                current = await ReadAsync(id, cancellationToken);
                if (current is not null)
                {
                    CheckDevice(current, deviceId);
                    if (IsAccepted(current))
                    {
                        return current.Receipt!;
                    }
                    if (current.State == ReportState.Preparing && current.AttemptId == attemptId)
                    {
                        try
                        {
                            await WriteAsync(current with
                            {
                                State = ReportState.Abandoned
                            },
                                                                                        current.Version, cancellationToken);
                        }
                        catch (RequestFailedException conflict) when (IsConditionFailure(conflict))
                        {
                            current = await ReadAsync(id, cancellationToken);
                            if (current is not null && IsAccepted(current))
                            {
                                CheckDevice(current, deviceId);
                                return current.Receipt!;
                            }
                        }
                    }
                }
                throw;
            }
        }

        public virtual async IAsyncEnumerable<SubmissionReport> GetPendingAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (BlobItem blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, BlobPrefix, cancellationToken))
            {
                if (!TryGetRecordId(blob.Name, out string id))
                {
                    continue;
                }
                Document? document = await ReadAsync(id, cancellationToken);
                if (document?.State == ReportState.Accepted)
                {
                    yield return ToReport(document);
                }
            }
        }

        public virtual async Task<SubmissionReport> GetForModerationAsync(string id, CancellationToken cancellationToken = default)
        {
            return ToReport(await ReadModeratableAsync(id, cancellationToken));
        }

        public virtual async Task<SubmissionReport> AdoptLegacyAsync(string legacySubmissionId, IReadOnlyList<SubmissionPhoto> photos,
            CancellationToken cancellationToken = default)
        {
            string id = LegacyReportId(legacySubmissionId);
            Document? existing = await ReadAsync(id, cancellationToken);
            if (existing is not null)
            {
                return ExistingLegacy(existing, legacySubmissionId);
            }
            ArgumentNullException.ThrowIfNull(photos);
            ValidatePhotos(photos, id, null, legacy: true);
            foreach (SubmissionPhoto photo in photos)
            {
                if (photo.Metadata.SubmissionId != legacySubmissionId ||
                                                    !IsSafeComponent(photo.Metadata.PhotoId) ||
                                                    photo.BlobName != $"{FinalizedUploadPrefix}{photo.Metadata.PhotoId}.jpeg")
                {
                    throw new ArgumentException("Legacy photo references must match their original metadata.", nameof(photos));
                }
                await VerifyPhotoAsync(photo.BlobName, photo.ETag, photo.Length, cancellationToken);
            }

            List<SubmissionPhoto> sanitized = photos.Select(p => p with
            {
                Metadata = CloneMetadata(p.Metadata, id, null)
            }).ToList();
            CheckMetadataSize(sanitized);
            string? did = sanitized[0].Metadata.Attribute == true ? sanitized[0].Metadata.BlueskyUserDid : null;
            SubmissionReceipt receipt = new SubmissionReceipt(id, legacySubmissionId, clock.GetUtcNow(), new ReportAttribution(BlueskyDid: did));
            Document accepted = new Document(id, null, ReportState.Accepted, clock.GetUtcNow(), null, sanitized, receipt,
                LegacySidecars: photos.Select(p => $"{FinalizedUploadPrefix}{p.Metadata.PhotoId}.json").ToList());
            try
            {
                return ToReport(await WriteAsync(accepted, null, cancellationToken));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                existing = await ReadAsync(id, cancellationToken);
                if (existing is not null)
                {
                    return ExistingLegacy(existing, legacySubmissionId);
                }
                throw;
            }
        }

        public virtual async Task<SubmissionReport> BeginModerationAsync(string id, string kind,
            IReadOnlyList<FinalizedPhotoUploadMetadata>? overrides = null, string? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            if (kind is not ("publishing" or "deleting"))
            {
                throw new ArgumentException("Unknown moderation operation.", nameof(kind));
            }
            Document document = await ReadModeratableAsync(id, cancellationToken);
            if (document.Moderation is not null)
            {
                throw new ReportConflictException("Another moderation operation owns this report; reconcile it before retrying.");
            }
            if (expectedVersion is not null && expectedVersion != document.Version)
            {
                throw new ReportConflictException("The report changed. Refresh before applying edits.");
            }
            List<SubmissionPhoto>? snapshot = null;
            if (overrides is not null)
            {
                if (overrides.Count != document.Photos.Count)
                {
                    throw new ArgumentException("Moderation must include the entire canonical photo set.", nameof(overrides));
                }
                snapshot = new List<SubmissionPhoto>();
                for (int index = 0; index < overrides.Count; index++)
                {
                    FinalizedPhotoUploadMetadata replacement = overrides[index];
                    SubmissionPhoto original = document.Photos[index];
                    RejectSecrets(replacement, allowTwitterLink: true);
                    if (replacement.PhotoId != original.Metadata.PhotoId ||
                        replacement.SubmissionId != original.Metadata.SubmissionId ||
                        replacement.PhotoNumber != original.Metadata.PhotoNumber)
                    {
                        throw new ArgumentException("Moderation cannot replace, reorder, or remove photos.", nameof(overrides));
                    }
                    if (replacement.PhotoDateTime is null || replacement.NumberOfCars is null or <= 0 ||
                                        !Coordinate(replacement.PhotoLatitude, 47.495082, 47.735525) ||
                                        !Coordinate(replacement.PhotoLongitude, -122.436522, -122.235787) ||
                                        !IsValidPublicationLink(replacement.TwitterLink) ||
                                        replacement.ReportId is not null && replacement.ReportId != id ||
                                        replacement.DeviceId is not null && replacement.DeviceId != document.DeviceId)
                    {
                        throw new ArgumentException("Invalid moderation metadata or submitting context.", nameof(overrides));
                    }
                    snapshot.Add(original with
                    {
                        Metadata = CloneMetadata(replacement, id, document.DeviceId, preserveTwitterLink: true)
                    });
                }
                CheckMetadataSize(snapshot);
            }

            ModerationOperation operation = new ModerationOperation(Guid.NewGuid().ToString("N"), kind, clock.GetUtcNow());
            try
            {
                return ToReport(await WriteAsync(document with
                {
                    Moderation = operation,
                    ModerationPhotos = snapshot
                },
                                                    document.Version, cancellationToken));
            }
            catch (RequestFailedException ex) when (IsConditionFailure(ex))
            {
                throw new ReportConflictException("Another operation changed this report.");
            }
        }

        public virtual async Task ReleaseModerationAsync(string id, string operationId, CancellationToken cancellationToken = default)
        {
            Document document = await ReadModeratableAsync(id, cancellationToken);
            CheckOperation(document, operationId);
            try
            {
                await WriteAsync(document with
                {
                    Moderation = null,
                    ModerationPhotos = null
                }, document.Version, cancellationToken);
            }
            catch (RequestFailedException ex) when (IsConditionFailure(ex))
            {
                throw new ReportConflictException("Another operation changed this report.");
            }
        }

        public virtual async Task RetireAsync(string id, string operationId, CancellationToken cancellationToken = default)
        {
            Document document = await ReadAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Report not found.");
            CheckOperation(document, operationId);
            if (document.State != ReportState.Retired)
            {
                if (document.State != ReportState.Accepted)
                {
                    throw new ReportConflictException("Only accepted reports can be retired.");
                }
                List<CleanupItem> inventory = document.Photos.Select(p => new CleanupItem(p.BlobName, p.ETag)).ToList();
                inventory.AddRange((document.LegacySidecars ?? []).Select(name => new CleanupItem(name, null)));
                try
                {
                    // Keep both the immutable receipt and a recoverable inventory before any deletion.
                    document = await WriteAsync(document with
                    {
                        State = ReportState.Retired,
                        Cleanup = inventory
                    },
                        document.Version, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Document? recovered = await ReadAsync(id, cancellationToken);
                    if (recovered?.State != ReportState.Retired || recovered.Moderation?.Id != operationId)
                    {
                        throw;
                    }
                    document = recovered;
                }
            }

            try
            {
                await CleanupRetiredAsync(document, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Retirement succeeded. Cleanup is retryable, not a reason to republish the report.
                logger.LogWarning(ex, "Cleanup remains pending for retired report {ReportId}.", id);
            }
        }

        public virtual async Task CleanupAsync(CancellationToken cancellationToken = default)
        {
            HashSet<string> unsafeReportIds = new HashSet<string>(StringComparer.Ordinal);
            await foreach (BlobItem blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, BlobPrefix, cancellationToken))
            {
                if (!TryGetRecordId(blob.Name, out string id) || unsafeReportIds.Contains(id))
                {
                    continue;
                }
                try
                {
                    Document? document = await ReadAsync(id, cancellationToken);
                    if (document is null)
                    {
                        continue;
                    }
                    if (document.State == ReportState.Preparing && !IsLive(document))
                    {
                        try
                        {
                            // Expiry alone cannot authorize deleting an active worker's reserved keys.
                            document = await WriteAsync(document with
                            {
                                State = ReportState.Abandoned
                            },
                                document.Version, cancellationToken);
                        }
                        catch (RequestFailedException ex) when (IsConditionFailure(ex))
                        {
                            continue;
                        }
                    }
                    if (document.State == ReportState.Retired)
                    {
                        await CleanupRetiredAsync(document, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                {
                    // Reconciliation reads after a failed coordinator write can also find corruption.
                    Quarantine(id, ex);
                }
            }

            // Repeat this inventory scan forever: an already-fenced worker's upload can complete
            // after an earlier sweep. Never collect on an absent coordinator or age alone.
            await foreach (BlobItem blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, PhotoPrefix, cancellationToken))
            {
                if (!blob.Name.StartsWith(PhotoPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                string[] parts = blob.Name[PhotoPrefix.Length..].Split('/');
                if (parts.Length != 3 || !IsValidReportId(parts[0]) || !IsValidReportId(parts[1]))
                {
                    continue;
                }
                if (unsafeReportIds.Contains(parts[0]))
                {
                    continue;
                }
                Document? document;
                try
                {
                    document = await ReadAsync(parts[0], cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                {
                    Quarantine(parts[0], ex);
                    continue;
                }
                if (document is null)
                {
                    continue;
                }
                if (document.State is ReportState.Preparing or ReportState.Accepted &&
                                (document.AttemptId == parts[1] || document.Photos.Any(p => p.BlobName == blob.Name) ||
                                 document.ModerationPhotos?.Any(p => p.BlobName == blob.Name) == true))
                {
                    continue;
                }
                await DeleteAsync(blob.Name, blob.Properties.ETag?.ToString(), cancellationToken);
            }

            void Quarantine(string id, Exception exception)
            {
                unsafeReportIds.Add(id);
                logger.LogWarning(exception,
                    "Skipping cleanup for invalid report {ReportId}; its record and possibly referenced photos are retained.", id);
            }
        }

        private async Task<SubmissionPhoto> CopyPhotoAsync(PreparedSubmissionPhoto source, SubmissionPhoto destination,
            CancellationToken cancellationToken)
        {
            using BlobDownloadStreamingResult download = await ReadSourceAsync(source, cancellationToken);
            if (download.Details.ContentLength != source.Length || download.Details.ETag.ToString() != source.ETag)
            {
                throw new PreparationExpiredException("The prepared photo changed or has an unexpected length.");
            }
            using ExactLengthReadStream stream = new ExactLengthReadStream(download.Content, source.Length);
            BlobClient destinationBlob = container.GetBlobClient(destination.BlobName);
            Response<BlobContentInfo> upload = await destinationBlob.UploadAsync(stream, new BlobUploadOptions()
            {
                Conditions = new BlobRequestConditions() { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders() { ContentType = "image/jpeg" },
                TransferValidation = new UploadTransferValidationOptions() { ChecksumAlgorithm = StorageChecksumAlgorithm.Auto },
                TransferOptions = new StorageTransferOptions()
                {
                    InitialTransferSize = 1024 * 1024,
                    MaximumTransferSize = 1024 * 1024,
                    MaximumConcurrency = 1
                }
            }, cancellationToken);
            await stream.VerifyCompleteAsync(cancellationToken);
            string etag = upload.Value.ETag.ToString();
            await VerifyPhotoAsync(destination.BlobName, etag, source.Length, cancellationToken);
            return destination with
            {
                ETag = etag
            };
        }

        private async Task<BlobDownloadStreamingResult> ReadSourceAsync(PreparedSubmissionPhoto source,
            CancellationToken cancellationToken)
        {
            try
            {
                return (await container.GetBlobClient(source.BlobName).DownloadStreamingAsync(
                                                    new BlobDownloadOptions() { Conditions = new BlobRequestConditions() { IfMatch = new ETag(source.ETag) } },
                                                    cancellationToken)).Value;
            }
            catch (RequestFailedException ex) when (IsExpiredSource(ex))
            {
                throw new PreparationExpiredException(innerException: ex);
            }
        }

        private async Task VerifyPhotoAsync(string name, string etag, long length, CancellationToken cancellationToken)
        {
            BlobProperties properties = (await container.GetBlobClient(name).GetPropertiesAsync(
                                        new BlobRequestConditions() { IfMatch = new ETag(etag) }, cancellationToken)).Value;
            if (properties.ContentLength != length || properties.ETag.ToString() != etag)
            {
                throw new InvalidDataException("The stored photo changed or has an unexpected length.");
            }
        }

        private async Task<Document?> ReadAsync(string id, CancellationToken cancellationToken)
        {
            if (!IsValidReportId(id))
            {
                throw new ArgumentException("Report IDs must contain 32 lowercase hexadecimal characters.", nameof(id));
            }
            BlobClient blob = container.GetBlobClient($"{BlobPrefix}{id}.json");
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    BlobProperties properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
                    if (properties.ContentLength is <= 0 or > MaxRecordBytes)
                    {
                        throw new InvalidDataException("The report record is empty or too large.");
                    }
                    if (string.IsNullOrWhiteSpace(properties.ETag.ToString()) || properties.ETag == ETag.All)
                    {
                        throw new IOException("Storage returned no usable version for the report record.");
                    }
                    using BlobDownloadStreamingResult result = (await blob.DownloadStreamingAsync(new BlobDownloadOptions()
                    {
                        Conditions = new BlobRequestConditions() { IfMatch = properties.ETag }
                    }, cancellationToken)).Value;
                    if (result.Details.ETag != properties.ETag)
                    {
                        throw new RequestFailedException(412, "The report changed while being read.", "ConditionNotMet", null);
                    }
                    if (result.Details.ContentLength != properties.ContentLength)
                    {
                        throw new InvalidDataException("The report stream has an unexpected length.");
                    }
                    // Neither a misleading length header nor an unbounded stream may grow this buffer.
                    byte[] content = new byte[(int)properties.ContentLength + 1];
                    int count = 0;
                    while (count < content.Length)
                    {
                        int read = await result.Content.ReadAsync(content.AsMemory(count), cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }
                        count += read;
                    }
                    if (count != properties.ContentLength)
                    {
                        throw new InvalidDataException("The report stream does not match its checked length.");
                    }
                    Document document = JsonSerializer.Deserialize<Document>(content.AsSpan(0, count))
                                        ?? throw new InvalidDataException("The report record is empty.");
                    ValidateStoredDocument(document, id);
                    return document with
                    {
                        Version = result.Details.ETag.ToString()
                    };
                }
                catch (RequestFailedException ex) when (ex.Status == 404 && ex.ErrorCode is null or "BlobNotFound")
                {
                    return null;
                }
                catch (RequestFailedException ex) when (attempt < 2 && ex.Status == 412 &&
                    ex.ErrorCode is null or "ConditionNotMet")
                {
                    // Retry a changed coordinator from its new properties, never as absence or corruption.
                }
            }
        }

        private static void ValidateStoredDocument(Document document, string id)
        {
            if (document.SchemaVersion != 1 || document.ReportId != id ||
                                        !Enum.IsDefined(document.State) || document.Photos is null ||
                                        !IsSafeStoredTime(document.StartedAt) ||
                                        document.AttemptId is not null && !IsValidReportId(document.AttemptId) ||
                                        document.MutationId is not null && !IsValidReportId(document.MutationId))
            {
                throw new InvalidDataException("Invalid report record.");
            }
            bool accepted = IsAccepted(document);
            bool legacy = document.AttemptId is null;
            if (!accepted)
            {
                if (legacy || document.Receipt is not null || document.Moderation is not null ||
                                                    document.ModerationPhotos is not null || document.LegacySidecars is not null || document.Cleanup is not null)
                {
                    throw new InvalidDataException("Invalid preparation record.");
                }
            }
            else
            {
                SubmissionReceipt? receipt = document.Receipt;
                if (receipt is null || receipt.ReportId != id || !IsSafeComponent(receipt.SubmissionId) ||
                    receipt.SubmittedAt == default || receipt.Attribution is null ||
                    legacy && (document.DeviceId is not null || LegacyReportId(receipt.SubmissionId) != id))
                {
                    throw new InvalidDataException("Invalid report receipt or legacy context.");
                }
                try
                {
                    ValidateAttribution(receipt.Attribution);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException("Invalid stored report attribution.", ex);
                }
            }

            if (document.Moderation is { } moderation &&
                (!accepted || !IsValidReportId(moderation.Id) ||
                 moderation.Kind is not ("publishing" or "deleting") || !IsSafeStoredTime(moderation.StartedAt)) ||
                document.ModerationPhotos is not null && document.Moderation is null)
            {
                throw new InvalidDataException("Invalid moderation ownership.");
            }
            if (document.State == ReportState.Retired)
            {
                if (document.Moderation is null || document.Cleanup is null)
                {
                    throw new InvalidDataException("Retired reports require ownership and a cleanup inventory.");
                }
                if (document.Photos.Count == 0)
                {
                    if (document.Cleanup.Count != 0 || document.ModerationPhotos is not null || document.LegacySidecars is not null)
                    {
                        throw new InvalidDataException("Invalid compacted retirement record.");
                    }
                    return;
                }
            }
            else if (document.Cleanup is not null)
            {
                throw new InvalidDataException("Only retired reports may authorize inventory cleanup.");
            }

            ValidateStoredPhotos(document, document.Photos, legacy, moderation: false);
            if (document.ModerationPhotos is { } snapshot)
            {
                ValidateStoredPhotos(document, snapshot, legacy, moderation: true);
                if (snapshot.Count != document.Photos.Count)
                {
                    throw new InvalidDataException("Invalid moderation photo inventory.");
                }
                for (int index = 0; index < snapshot.Count; index++)
                {
                    SubmissionPhoto original = document.Photos[index];
                    SubmissionPhoto replacement = snapshot[index];
                    if (replacement.BlobName != original.BlobName || replacement.ETag != original.ETag ||
                        replacement.Length != original.Length || replacement.Metadata.PhotoId != original.Metadata.PhotoId ||
                        replacement.Metadata.SubmissionId != original.Metadata.SubmissionId ||
                        replacement.Metadata.PhotoNumber != original.Metadata.PhotoNumber)
                    {
                        throw new InvalidDataException("Moderation cannot change stored photo references.");
                    }
                }
            }

            HashSet<string> sidecars = document.Photos.Select(p => $"{FinalizedUploadPrefix}{p.Metadata.PhotoId}.json")
                .ToHashSet(StringComparer.Ordinal);
            if (legacy)
            {
                if (document.LegacySidecars is null || document.LegacySidecars.Count != sidecars.Count ||
                                                    !sidecars.SetEquals(document.LegacySidecars))
                {
                    throw new InvalidDataException("Invalid legacy sidecar inventory.");
                }
            }
            else if (document.LegacySidecars is not null)
            {
                throw new InvalidDataException("Non-legacy reports cannot reference legacy sidecars.");
            }

            if (document.State == ReportState.Retired)
            {
                HashSet<CleanupItem> inventory = document.Photos.Select(p => new CleanupItem(p.BlobName, p.ETag)).ToHashSet();
                if (legacy)
                {
                    inventory.UnionWith(sidecars.Select(name => new CleanupItem(name, null)));
                }
                if (document.Cleanup!.Count != inventory.Count || !inventory.SetEquals(document.Cleanup))
                {
                    throw new InvalidDataException("Cleanup must match the retained photo and sidecar inventory.");
                }
            }
        }

        private static void ValidateStoredPhotos(Document document, IReadOnlyList<SubmissionPhoto> photos, bool legacy,
            bool moderation)
        {
            if (photos.Any(photo => photo is null || photo.Metadata is null))
            {
                throw new InvalidDataException("Missing stored photo or metadata.");
            }
            try
            {
                // A preparing/abandoned reservation has destination keys but no committed photo ETags yet.
                IReadOnlyList<SubmissionPhoto> checkedPhotos = IsAccepted(document) ? photos :
                    photos.Select(photo => photo.ETag == "" ? photo with { ETag = "reserved" } : photo).ToArray();
                ValidatePhotos(checkedPhotos, document.ReportId, document.DeviceId, legacy || moderation);
                foreach (SubmissionPhoto photo in photos)
                {
                    RejectSecrets(photo.Metadata, allowTwitterLink: moderation);
                }
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Invalid stored photo set.", ex);
            }

            for (int index = 0; index < photos.Count; index++)
            {
                SubmissionPhoto photo = photos[index];
                FinalizedPhotoUploadMetadata metadata = photo.Metadata;
                string expectedPath = legacy ? $"{FinalizedUploadPrefix}{metadata.PhotoId}.jpeg" :
                    $"{PhotoPrefix}{document.ReportId}/{document.AttemptId}/{index}.jpeg";
                if (photo.BlobName != expectedPath || metadata.ReportId != document.ReportId ||
                    metadata.DeviceId != document.DeviceId ||
                    document.Receipt is { } receipt && metadata.SubmissionId != receipt.SubmissionId)
                {
                    throw new InvalidDataException("Invalid stored photo reference or submitting context.");
                }
                if (moderation && (metadata.PhotoDateTime is null || metadata.NumberOfCars is null or <= 0 ||
                                !Coordinate(metadata.PhotoLatitude, 47.495082, 47.735525) ||
                                !Coordinate(metadata.PhotoLongitude, -122.436522, -122.235787) ||
                                !IsValidPublicationLink(metadata.TwitterLink)))
                {
                    throw new InvalidDataException("Invalid stored moderation metadata.");
                }
            }
        }

        private static bool IsSafeStoredTime(DateTimeOffset time)
        {
            return time != default &&
                                    time.Ticks <= DateTime.MaxValue.Ticks - PreparationTimeout.Ticks &&
                                    time.UtcTicks <= DateTime.MaxValue.Ticks - PreparationTimeout.Ticks;
        }

        private async Task<Document> WriteAsync(Document document, string? expectedVersion, CancellationToken cancellationToken)
        {
            document = document with
            {
                MutationId = Guid.NewGuid().ToString("N"),
                Version = null
            };
            BinaryData content = BinaryData.FromObjectAsJson(document);
            if (content.ToMemory().Length > MaxRecordBytes)
            {
                throw new ArgumentException("Report metadata is too large.");
            }
            try
            {
                Response<BlobContentInfo> response = await container.GetBlobClient($"{BlobPrefix}{document.ReportId}.json")
                                                    .UploadAsync(content, new BlobUploadOptions()
                                                    {
                                                        Conditions = expectedVersion is null
                                                            ? new BlobRequestConditions() { IfNoneMatch = ETag.All }
                                                            : new BlobRequestConditions() { IfMatch = new ETag(expectedVersion) },
                                                        HttpHeaders = new BlobHttpHeaders() { ContentType = "application/json" }
                                                    }, cancellationToken);
                return document with
                {
                    Version = response.Value.ETag.ToString()
                };
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Document? recovered = await ReadAsync(document.ReportId, cancellationToken);
                if (recovered?.MutationId == document.MutationId)
                {
                    return recovered;
                }
                throw;
            }
        }

        private async Task<Document> ReadModeratableAsync(string id, CancellationToken cancellationToken)
        {
            Document document = await ReadAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Report not found.");
            if (document.State == ReportState.Preparing)
            {
                throw InProgress(document);
            }
            if (document.State != ReportState.Accepted)
            {
                throw new ReportConflictException("This report is not pending moderation.");
            }
            return document;
        }

        private async Task CleanupRetiredAsync(Document document, CancellationToken cancellationToken)
        {
            if (document.Cleanup is null || document.Cleanup.Count == 0)
            {
                return;
            }
            foreach (CleanupItem item in document.Cleanup)
            {
                await DeleteAsync(item.BlobName, item.ETag, cancellationToken);
            }
            try
            {
                await WriteAsync(document with
                {
                    Photos = [],
                    ModerationPhotos = null,
                    LegacySidecars = null,
                    Cleanup = []
                }, document.Version, cancellationToken);
            }
            catch (RequestFailedException ex) when (IsConditionFailure(ex))
            {
                // Concurrent cleanup can compact the same tombstone. No state can restore it.
            }
        }

        private async Task DeleteAsync(string name, string? etag, CancellationToken cancellationToken)
        {
            await container.GetBlobClient(name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots,
                                        etag is null ? null : new BlobRequestConditions() { IfMatch = new ETag(etag) }, cancellationToken);
        }

        private bool IsLive(Document document)
        {
            return document.State == ReportState.Preparing && document.StartedAt + PreparationTimeout > clock.GetUtcNow();
        }

        private ReportInProgressException InProgress(Document document)
        {
            return new ReportInProgressException(TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(
                                        (document.StartedAt + PreparationTimeout - clock.GetUtcNow()).TotalSeconds))));
        }

        private static bool IsAccepted(Document document)
        {
            return document.State is ReportState.Accepted or ReportState.Retired;
        }

        private static bool IsConditionFailure(RequestFailedException ex)
        {
            return ex.Status == 412 && ex.ErrorCode is null or "ConditionNotMet" ||
                                    ex.Status == 409 && ex.ErrorCode is null or "BlobAlreadyExists";
        }

        private static bool IsExpiredSource(RequestFailedException ex)
        {
            return ex.Status == 404 && ex.ErrorCode is null or "BlobNotFound" ||
                                    ex.Status == 412 && ex.ErrorCode is null or "ConditionNotMet";
        }

        private static SubmissionReport ToReport(Document document)
        {
            return new SubmissionReport(document.Receipt!, document.DeviceId, document.ModerationPhotos ?? document.Photos,
                                        document.State == ReportState.Retired, document.Moderation)
            {
                Version = document.Version
            };
        }

        private static void CheckDevice(Document document, string? deviceId)
        {
            if (!string.Equals(document.DeviceId, deviceId, StringComparison.Ordinal))
            {
                throw new ReportDeviceMismatchException();
            }
        }

        private static void CheckOperation(Document document, string operationId)
        {
            if (document.Moderation is null || document.Moderation.Id != operationId)
            {
                throw new ReportConflictException("This operation does not own the report.");
            }
        }

        private static SubmissionReport ExistingLegacy(Document document, string legacySubmissionId)
        {
            CheckDevice(document, null);
            if (!IsAccepted(document) || document.Receipt?.SubmissionId != legacySubmissionId)
            {
                throw new ReportConflictException("The legacy report ID is already reserved.");
            }
            return ToReport(document);
        }

        private static bool TryGetRecordId(string name, out string id)
        {
            id = name.StartsWith(BlobPrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)
                                        ? name[BlobPrefix.Length..^5] : "";
            return IsValidReportId(id);
        }

        private static bool IsSafeComponent(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
                                    value is not ("." or "..") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
        }

        private static bool IsPhotoPath(string name, string prefix)
        {
            return !string.IsNullOrEmpty(name) && name.StartsWith(prefix, StringComparison.Ordinal) && name.Length <= 512 &&
                                    name[prefix.Length..].Split('/').All(part => part.Length > 0 && part is not ("." or "..")) &&
                                    !name.Contains('\\') && !name.Contains('?') && !name.Contains('#') &&
                                    (name.EndsWith(".jpeg", StringComparison.Ordinal) || name.EndsWith(".jpg", StringComparison.Ordinal));
        }

        private static void ValidateAttribution(ReportAttribution attribution)
        {
            ArgumentNullException.ThrowIfNull(attribution);
            if (attribution.BlueskyDid is { } did &&
                (!did.StartsWith("did:", StringComparison.Ordinal) || did.Length > 512 || did.Any(char.IsWhiteSpace)))
            {
                throw new ArgumentException("Invalid Bluesky attribution.", nameof(attribution));
            }
            if ((attribution.MastodonServer is null) != (attribution.MastodonAccountId is null))
            {
                throw new ArgumentException("Mastodon attribution requires a server and account ID.", nameof(attribution));
            }
            if (attribution.MastodonServer is { } server &&
                        (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
                         uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                         string.IsNullOrWhiteSpace(attribution.MastodonAccountId) || attribution.MastodonAccountId.Length > 128))
            {
                throw new ArgumentException("Invalid Mastodon attribution.", nameof(attribution));
            }
        }

        private static void ValidatePhotos(IReadOnlyList<SubmissionPhoto> photos, string id, string? deviceId, bool legacy)
        {
            if (photos.Count is < 1 or > 4)
            {
                throw new ArgumentException("A report must contain one to four photos.", nameof(photos));
            }
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            for (int index = 0; index < photos.Count; index++)
            {
                SubmissionPhoto photo = photos[index];
                FinalizedPhotoUploadMetadata metadata = photo.Metadata ?? throw new ArgumentException("Missing photo metadata.");
                if (!IsSafeComponent(metadata.PhotoId) || !IsSafeComponent(metadata.SubmissionId) ||
                    !ids.Add(metadata.PhotoId) || !paths.Add(photo.BlobName) ||
                    string.IsNullOrWhiteSpace(photo.ETag) || photo.ETag == "*" ||
                    photo.Length is <= 0 or > MaxPhotoBytes ||
                    (total += photo.Length) > MaxReportBytes ||
                    metadata.SubmissionId != photos[0].Metadata.SubmissionId ||
                    metadata.PhotoNumber < 0 || (index > 0 && metadata.PhotoNumber <= photos[index - 1].Metadata.PhotoNumber))
                {
                    throw new ArgumentException("Invalid photo set, versions, order, or sizes.", nameof(photos));
                }
                if (legacy)
                {
                    continue;
                }
                RejectSecrets(metadata);
                if (metadata.PhotoNumber != index ||
                    metadata.ReportId is not null && metadata.ReportId != id ||
                    metadata.DeviceId is not null && metadata.DeviceId != deviceId ||
                    metadata.PhotoDateTime is null || metadata.NumberOfCars is null or <= 0 ||
                    !Coordinate(metadata.PhotoLatitude, 47.495082, 47.735525) ||
                    !Coordinate(metadata.PhotoLongitude, -122.436522, -122.235787) ||
                    metadata.NumberOfCars != photos[0].Metadata.NumberOfCars ||
                    metadata.Attribute != photos[0].Metadata.Attribute ||
                    metadata.BlueskyUserDid != photos[0].Metadata.BlueskyUserDid ||
                    metadata.MastodonFullUsername != photos[0].Metadata.MastodonFullUsername)
                {
                    throw new ArgumentException("Invalid report metadata or submitting context.", nameof(photos));
                }
            }
        }

        private static bool Coordinate(string? value, double minimum, double maximum)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
                                    double.IsFinite(number) && number >= minimum && number <= maximum;
        }

        private static bool IsValidPublicationLink(string? value)
        {
            return string.IsNullOrEmpty(value) ||
                                    Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" &&
                                    uri.Host.Length > 0 && uri.UserInfo.Length == 0;
        }

        private static void CheckMetadataSize(List<SubmissionPhoto> photos)
        {
            // Reserve room for an independent moderation snapshot and retirement inventory, so
            // accepting a near-limit report cannot make its later lifecycle transitions impossible.
            if (BinaryData.FromObjectAsJson(photos).ToMemory().Length > MaxRecordBytes / 3)
            {
                throw new ArgumentException("Report metadata is too large.");
            }
        }

        private static void RejectSecrets(FinalizedPhotoUploadMetadata metadata, bool allowTwitterLink = false)
        {
            if (!string.IsNullOrEmpty(metadata.TwitterAccessToken) || !string.IsNullOrEmpty(metadata.MastodonAccessToken) ||
                                        !string.IsNullOrEmpty(metadata.ThreadsAccessToken) || !string.IsNullOrEmpty(metadata.BlueskyAccessJwt) ||
                                        !string.IsNullOrEmpty(metadata.BlueskyAdminDid) ||
                                        !allowTwitterLink && !string.IsNullOrEmpty(metadata.TwitterLink))
            {
                throw new ArgumentException("Credentials and administrator-only fields cannot be stored in a report.");
            }
        }

        private static FinalizedPhotoUploadMetadata CloneMetadata(FinalizedPhotoUploadMetadata metadata, string id, string? deviceId,
            bool preserveTwitterLink = false)
        {
            FinalizedPhotoUploadMetadata clone = JsonSerializer.Deserialize<FinalizedPhotoUploadMetadata>(
                                        JsonSerializer.Serialize(metadata))!;
            clone.TwitterAccessToken = null;
            clone.MastodonAccessToken = null;
            clone.ThreadsAccessToken = null;
            clone.BlueskyAccessJwt = null;
            clone.BlueskyAdminDid = null;
            if (!preserveTwitterLink)
            {
                clone.TwitterLink = null;
            }
            clone.ReportId = id;
            clone.DeviceId = deviceId;
            return clone;
        }

        private sealed class ExactLengthReadStream : Stream
        {
            private readonly Stream inner;
            private readonly long expectedLength;
            private long count;

            public ExactLengthReadStream(Stream inner, long expectedLength)
            {
                this.inner = inner;
                this.expectedLength = expectedLength;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get
                {
                    return count;
                }
                set
                {
                    throw new NotSupportedException();
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return Read(buffer.AsSpan(offset, count));
            }

            public override int Read(Span<byte> buffer)
            {
                try
                {
                    return buffer.IsEmpty ? 0 : Check(inner.Read(buffer[..(int)Math.Min(buffer.Length, expectedLength - count + 1)]));
                }
                catch (RequestFailedException ex) when (IsExpiredSource(ex))
                {
                    throw new PreparationExpiredException(innerException: ex);
                }
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                try
                {
                    return buffer.IsEmpty ? 0 :
                                                                Check(await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, expectedLength - count + 1)], cancellationToken));
                }
                catch (RequestFailedException ex) when (IsExpiredSource(ex))
                {
                    throw new PreparationExpiredException(innerException: ex);
                }
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            }

            private int Check(int read)
            {
                count += read;
                if (count > expectedLength || read == 0 && count != expectedLength)
                {
                    throw new PreparationExpiredException("Photo stream does not match its checked length.");
                }
                return read;
            }

            public async Task VerifyCompleteAsync(CancellationToken cancellationToken)
            {
                if (count != expectedLength || await ReadAsync(new byte[1], cancellationToken) != 0)
                {
                    throw new InvalidDataException("Photo stream does not match its checked length.");
                }
            }

            public override void Flush()
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
