using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Providers;

public sealed record SubmissionPhoto(FinalizedPhotoUploadMetadata Metadata, byte[] Jpeg, string Sha256);
public sealed record ModerationOperation(string Id, string Kind, DateTimeOffset StartedAt);

public sealed record SubmissionReport(
    SubmissionReceipt Receipt,
    string DeviceId,
    List<SubmissionPhoto> Photos,
    bool Retired = false,
    ModerationOperation? Moderation = null);

/// <summary>
/// The entire mobile report is one conditional blob write: no partial photo set can be published.
/// Retiring a moderated report replaces its bytes with a permanent deduplication receipt.
/// </summary>
public sealed class SubmissionClaimProvider
{
    public const string BlobPrefix = "mobilereports/";
    public const int MaxPhotoBytes = 8 * 1024 * 1024;
    public const int MaxReportBytes = 4 * MaxPhotoBytes;
    private const long MaxStoredBytes = 48L * 1024 * 1024;
    private readonly BlobContainerClient container;

    public SubmissionClaimProvider(ILogger<SubmissionClaimProvider> logger, BlobContainerClient container)
    {
        this.container = container;
    }

    public static bool IsValidReportId(string? id) =>
        id is { Length: 32 } && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    public async Task<SubmissionReport?> GetAsync(string id, string? deviceId,
        CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(id, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        if (!string.Equals(stored.Value.Report.DeviceId, deviceId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The report belongs to another installation.");
        }

        return stored.Value.Report;
    }

    public async Task<SubmissionReceipt> CommitAsync(SubmissionReport report,
        CancellationToken cancellationToken = default)
    {
        Validate(report);
        BinaryData bundle = BinaryData.FromObjectAsJson(report);
        if (bundle.ToMemory().Length > MaxStoredBytes)
        {
            throw new InvalidDataException("Mobile report exceeds its storage limit.");
        }
        try
        {
            await Blob(report.Receipt.ReportId).UploadAsync(bundle,
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
                cancellationToken);
            return report.Receipt;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // A competing request may have used different preparation IDs or credentials.
            // Its first accepted bundle, including attribution, is the only authoritative one.
            return (await GetAsync(report.Receipt.ReportId, report.DeviceId, cancellationToken)
                ?? throw new IOException("The accepted report could not be read.")).Receipt;
        }
        // All other errors are uncertain, not permission to commit elsewhere. The client reconciles
        // by report ID; a lost success response finds the same bundle on the next request.
    }

    public async IAsyncEnumerable<SubmissionReport> GetPendingAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (BlobItem blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None,
            BlobPrefix, cancellationToken))
        {
            string id = Path.GetFileNameWithoutExtension(blob.Name);
            var stored = await ReadAsync(id, cancellationToken)
                ?? throw new IOException("A listed mobile report could not be read.");
            if (!stored.Report.Retired)
            {
                yield return stored.Report;
            }
        }
    }

    public async Task<SubmissionReport> GetForModerationAsync(string id,
        CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(id, cancellationToken)
            ?? throw new FileNotFoundException("The mobile report does not exist.");
        if (stored.Report.Retired)
        {
            throw new InvalidOperationException("The mobile report has already been moderated.");
        }

        return stored.Report;
    }

    public async Task<SubmissionReport> BeginModerationAsync(string id, string kind,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("publishing" or "deleting")) throw new ArgumentException("Invalid moderation operation.");
        var stored = await ReadAsync(id, cancellationToken)
            ?? throw new IOException("The mobile report does not exist.");
        if (stored.Report.Retired || stored.Report.Moderation is not null)
            throw new InvalidOperationException("This report is already owned by moderation; reconcile an interrupted operation before retrying.");
        SubmissionReport owned = stored.Report with
        {
            Moderation = new ModerationOperation(
            Guid.NewGuid().ToString("N"), kind, DateTimeOffset.UtcNow)
        };
        await Blob(id).UploadAsync(BinaryData.FromObjectAsJson(owned),
            new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = stored.ETag } }, cancellationToken);
        return owned;
    }

    public async Task ReleaseModerationAsync(string id, string operationId, CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(id, cancellationToken)
            ?? throw new IOException("The mobile report does not exist.");
        if (stored.Report.Retired) return;
        if (stored.Report.Moderation?.Id != operationId)
            throw new InvalidOperationException("The moderation operation no longer owns this report.");
        await Blob(id).UploadAsync(BinaryData.FromObjectAsJson(stored.Report with { Moderation = null }),
            new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = stored.ETag } }, cancellationToken);
    }

    public async Task RetireAsync(string id, string? operationId = null, CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(id, cancellationToken)
            ?? throw new IOException("Cannot retire a missing mobile report.");
        if (stored.Report.Retired)
        {
            return;
        }
        if (stored.Report.Moderation?.Id != operationId)
            throw new InvalidOperationException("Only the owning moderation operation may retire this report.");

        SubmissionReport receiptOnly = stored.Report with { Photos = [], Retired = true, Moderation = null };
        try
        {
            await Blob(id).UploadAsync(BinaryData.FromObjectAsJson(receiptOnly),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = stored.ETag } },
                cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            var current = await ReadAsync(id, cancellationToken);
            if (current?.Report.Retired != true)
            {
                throw;
            }
        }
    }

    private async Task<(SubmissionReport Report, ETag ETag)?> ReadAsync(string id, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await ReadVersionAsync(id, token); }
            catch (RequestFailedException ex) when (ex.Status == 412 && attempt < 3)
            {
                // Ownership/compaction may change the version between properties and content.
                // Re-read a coherent version, never infer absence from a conditional failure.
            }
        }
    }

    private async Task<(SubmissionReport Report, ETag ETag)?> ReadVersionAsync(string id, CancellationToken token)
    {
        BlobProperties properties;
        try
        {
            properties = await Blob(id).GetPropertiesAsync(cancellationToken: token);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        if (properties.ContentLength > MaxStoredBytes)
        {
            throw new InvalidDataException("Mobile report exceeds its storage limit.");
        }
        // Bound reads before deserializing base64 photo data.
        using Stream input = await Blob(id).OpenReadAsync(new BlobOpenReadOptions(false)
        {
            Conditions = new BlobRequestConditions { IfMatch = properties.ETag }
        }, token);
        using MemoryStream bytes = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + count > MaxStoredBytes)
            {
                throw new InvalidDataException("Mobile report exceeds its storage limit.");
            }
            await bytes.WriteAsync(buffer.AsMemory(0, count), token);
        }
        bytes.Position = 0;
        SubmissionReport report = await JsonSerializer.DeserializeAsync<SubmissionReport>(bytes,
            cancellationToken: token) ?? throw new InvalidDataException("Mobile report is empty.");
        Validate(report);
        if (report.Receipt.ReportId != id)
        {
            throw new InvalidDataException("Mobile report identity does not match its storage key.");
        }
        return (report, properties.ETag);
    }

    private BlobClient Blob(string id)
    {
        if (!IsValidReportId(id))
        {
            throw new ArgumentException("Invalid report ID.", nameof(id));
        }
        return container.GetBlobClient($"{BlobPrefix}{id}.json");
    }

    private static void Validate(SubmissionReport report)
    {
        if (report.Receipt is null || !IsValidReportId(report.Receipt.ReportId) ||
            report.Receipt.SubmissionId != report.Receipt.ReportId || report.Receipt.SubmittedAt == default ||
            string.IsNullOrWhiteSpace(report.DeviceId) || report.Receipt.Attribution is null ||
            report.Photos is null || (report.Retired ? report.Photos.Count != 0 : report.Photos.Count is < 1 or > 4))
        {
            throw new InvalidDataException("Invalid mobile report.");
        }
        long total = 0;
        for (int i = 0; i < report.Photos.Count; i++)
        {
            SubmissionPhoto photo = report.Photos[i];
            if (photo?.Metadata is not { } metadata || photo.Jpeg is not { Length: > 0 } ||
                photo.Jpeg.Length > MaxPhotoBytes || (total += photo.Jpeg.Length) > MaxReportBytes ||
                metadata.PhotoId != $"{report.Receipt.ReportId}-{i}" || metadata.PhotoNumber != i ||
                metadata.ReportId != report.Receipt.ReportId || metadata.SubmissionId != report.Receipt.SubmissionId ||
                metadata.DeviceId != report.DeviceId ||
                metadata.TwitterAccessToken is not null || metadata.MastodonAccessToken is not null ||
                metadata.ThreadsAccessToken is not null || metadata.BlueskyAccessJwt is not null ||
                Convert.ToHexString(SHA256.HashData(photo.Jpeg)) != photo.Sha256)
            {
                throw new InvalidDataException("Invalid or incomplete mobile report photo.");
            }
        }
    }
}
