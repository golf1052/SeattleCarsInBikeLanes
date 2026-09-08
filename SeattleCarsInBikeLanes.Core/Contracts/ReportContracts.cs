namespace SeattleCarsInBikeLanes.Core.Contracts
{
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

    public sealed record FinalizeReportRequest(
        List<FinalizedPhotoUpload> Photos,
        ReportAttribution Attribution);

    public sealed record CredentialIdentity(string AccountId, string DisplayName, DateTimeOffset? ExpiresAt = null);

    public static class UploadErrors
    {
        public const string CredentialRejected = "credential_rejected";
        public const string IdentityMismatch = "identity_mismatch";
        public const string ProviderUnavailable = "provider_unavailable";
        public const string ReportInProgress = "report_in_progress";
        public const string PreparationExpired = "preparation_expired";
    }

    public sealed record UploadError(string Code, string Message);
}
