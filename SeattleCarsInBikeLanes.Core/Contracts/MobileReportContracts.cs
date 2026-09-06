namespace SeattleCarsInBikeLanes.Core.Contracts;

/// <summary>Non-secret constraints, never evidence of an authenticated identity.</summary>
public sealed record ReportAttribution(
    string? BlueskyDid = null,
    string? MastodonServer = null,
    string? MastodonAccountId = null)
{
    public bool IsAnonymous => BlueskyDid is null && MastodonAccountId is null && MastodonServer is null;
}

public sealed record SubmissionReceipt(
    string ReportId,
    string SubmissionId,
    DateTimeOffset SubmittedAt,
    ReportAttribution Attribution);

public sealed record MobileFinalizeRequest(
    List<FinalizedPhotoUpload> Photos,
    ReportAttribution Attribution);

public sealed record CredentialIdentity(string AccountId, string DisplayName, DateTimeOffset? ExpiresAt = null);

public static class MobileUploadErrors
{
    public const string CredentialRejected = "credential_rejected";
    public const string IdentityMismatch = "identity_mismatch";
    public const string ProviderUnavailable = "provider_unavailable";
}

public sealed record MobileUploadError(string Code, string Message);
