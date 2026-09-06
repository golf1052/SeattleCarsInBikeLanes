namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Where the app talks to the site.
/// </summary>
public static class SiteUrls
{
    public static Uri BaseAddress { get; } = new Uri("https://seattle.carinbikelane.com/");

    public static Uri Privacy { get; } = new Uri("https://carinbikelane.com/privacy");

    /// <summary>
    /// The map, embedded rather than rebuilt. The query parameter lets the site tell app traffic
    /// apart and hide anything that does not belong in a native shell.
    /// </summary>
    public static Uri Map { get; } = new Uri(BaseAddress, "?mobileAuth=1");

    public static Uri BlueskyMe { get; } = new Uri(BaseAddress, "api/BlueskyAuth/me");
    public static Uri BlueskyNativeMe { get; } = new Uri(BaseAddress, "api/BlueskyAuth/native-me");

    public static Uri BlueskyToken { get; } = new Uri(BaseAddress, "api/BlueskyAuth/token");

    public static Uri BlueskyLogout { get; } = new Uri(BaseAddress, "api/BlueskyAuth/logout");

    public static Uri UploadInitial { get; } = new Uri(BaseAddress, "api/Upload/Initial");

    public static Uri UploadFinalize { get; } = new Uri(BaseAddress, "api/Upload/FinalizeMobile");
    public static Uri ReportStatus(string reportId) => new Uri(BaseAddress, $"api/Upload/Reports/{reportId}");

    public static Uri UploadLimits { get; } = new Uri(BaseAddress, "api/Upload/Limits");

    /// <summary>
    /// Verifies a Mastodon access token and returns the account it belongs to.
    /// </summary>
    public static Uri MastodonUsername { get; } = new Uri(BaseAddress, "api/Mastodon/GetMastodonUsername");
    public static Uri MastodonNativeIdentity { get; } = new Uri(BaseAddress, "api/Mastodon/NativeIdentity");
}
