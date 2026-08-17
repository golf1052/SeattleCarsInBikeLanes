using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace SeattleCarsInBikeLanes.Providers
{
    public enum SubmissionClaimResult
    {
        Claimed,
        AlreadyCompleted,
        InFlight
    }

    /// <summary>
    /// Makes finalizing a mobile report idempotent across retries and server instances.
    /// </summary>
    public sealed class SubmissionClaimProvider
    {
        public const string BlobPrefix = "submissionclaims/";

        private const string InProgressStatus = "InProgress";
        private const string CompletedStatus = "Completed";
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

        public static TimeSpan InFlightRetryAfter => StaleAfter;

        private readonly ILogger<SubmissionClaimProvider> logger;
        private readonly BlobContainerClient blobContainerClient;

        public SubmissionClaimProvider(ILogger<SubmissionClaimProvider> logger,
            BlobContainerClient blobContainerClient)
        {
            this.logger = logger;
            this.blobContainerClient = blobContainerClient;
        }

        public static bool IsValidReportId(string? reportId)
        {
            if (reportId is null || reportId.Length != 32)
            {
                return false;
            }

            foreach (char character in reportId)
            {
                if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        public async Task<SubmissionClaimResult> TryClaimAsync(string reportId,
            string? deviceId,
            CancellationToken cancellationToken = default)
        {
            if (!IsValidReportId(reportId))
            {
                throw new ArgumentException("Report ids must be 32 lowercase hexadecimal characters.",
                    nameof(reportId));
            }

            BlobClient blobClient = blobContainerClient.GetBlobClient(GetBlobName(reportId));
            SubmissionClaim claim = SubmissionClaim.InProgress(deviceId, DateTimeOffset.UtcNow);

            try
            {
                await UploadAsync(blobClient,
                    claim,
                    new BlobRequestConditions { IfNoneMatch = ETag.All },
                    cancellationToken);
                return SubmissionClaimResult.Claimed;
            }
            catch (RequestFailedException ex)
                when (ex.Status is StatusCodes.Status409Conflict or StatusCodes.Status412PreconditionFailed)
            {
                return await ResolveExistingClaimAsync(blobClient,
                    reportId,
                    deviceId,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not create the submission claim for report {ReportId}; finalizing without idempotency.",
                    reportId);
                return SubmissionClaimResult.Claimed;
            }
        }

        public async Task CompleteAsync(string reportId, string? deviceId)
        {
            if (!IsValidReportId(reportId))
            {
                throw new ArgumentException("Report ids must be 32 lowercase hexadecimal characters.",
                    nameof(reportId));
            }

            try
            {
                BlobClient blobClient = blobContainerClient.GetBlobClient(GetBlobName(reportId));
                await blobClient.UploadAsync(
                    BinaryData.FromObjectAsJson(SubmissionClaim.Completed(deviceId, DateTimeOffset.UtcNow)),
                    overwrite: true);
            }
            catch (Exception ex)
            {
                // The report is already finalized. Losing the marker only loses duplicate protection;
                // it must not make the client resend an otherwise successful report.
                logger.LogError(ex, "Could not complete the submission claim for report {ReportId}.", reportId);
            }
        }

        private async Task<SubmissionClaimResult> ResolveExistingClaimAsync(BlobClient blobClient,
            string reportId,
            string? deviceId,
            CancellationToken cancellationToken)
        {
            try
            {
                BlobDownloadResult download = await blobClient.DownloadContentAsync(cancellationToken);
                SubmissionClaim? existing = download.Content.ToObjectFromJson<SubmissionClaim>();
                DateTimeOffset now = DateTimeOffset.UtcNow;

                if (string.Equals(existing?.Status, CompletedStatus, StringComparison.Ordinal))
                {
                    return SubmissionClaimResult.AlreadyCompleted;
                }

                if (existing is not null &&
                    string.Equals(existing.Status, InProgressStatus, StringComparison.Ordinal) &&
                    existing.ClaimedAt > now - StaleAfter)
                {
                    return SubmissionClaimResult.InFlight;
                }

                SubmissionClaim takeover = SubmissionClaim.InProgress(deviceId, now);
                try
                {
                    await UploadAsync(blobClient,
                        takeover,
                        new BlobRequestConditions { IfMatch = download.Details.ETag },
                        cancellationToken);
                    return SubmissionClaimResult.Claimed;
                }
                catch (RequestFailedException ex)
                    when (ex.Status is StatusCodes.Status409Conflict or StatusCodes.Status412PreconditionFailed)
                {
                    return SubmissionClaimResult.InFlight;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not read the submission claim for report {ReportId}; finalizing without idempotency.",
                    reportId);
            }

            return SubmissionClaimResult.Claimed;
        }

        private static Task UploadAsync(BlobClient blobClient,
            SubmissionClaim claim,
            BlobRequestConditions conditions,
            CancellationToken cancellationToken) =>
            blobClient.UploadAsync(
                BinaryData.FromObjectAsJson(claim),
                new BlobUploadOptions { Conditions = conditions },
                cancellationToken);

        private static string GetBlobName(string reportId) => $"{BlobPrefix}{reportId}.json";

        private sealed class SubmissionClaim
        {
            public SubmissionClaim()
            {
            }

            public string? Status { get; set; }

            public string? DeviceId { get; set; }

            public DateTimeOffset ClaimedAt { get; set; }

            public DateTimeOffset? CompletedAt { get; set; }

            public static SubmissionClaim InProgress(string? deviceId, DateTimeOffset now) =>
                new SubmissionClaim
                {
                    Status = InProgressStatus,
                    DeviceId = deviceId,
                    ClaimedAt = now
                };

            public static SubmissionClaim Completed(string? deviceId, DateTimeOffset now) =>
                new SubmissionClaim
                {
                    Status = CompletedStatus,
                    DeviceId = deviceId,
                    ClaimedAt = now,
                    CompletedAt = now
                };
        }
    }
}
