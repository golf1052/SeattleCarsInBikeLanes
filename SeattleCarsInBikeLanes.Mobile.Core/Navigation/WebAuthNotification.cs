using System.Web;

namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

/// <summary>
/// Parses authentication notifications emitted by the embedded website.
/// </summary>
public static class WebAuthNotification
{
    private const string Scheme = "cibl-mobile";
    private const string Host = "auth";
    private const string SignedOutPath = "/signed-out";

    public static bool IsNotificationScheme(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetSignedOutProvider(Uri target, out WebAuthProvider provider)
    {
        ArgumentNullException.ThrowIfNull(target);

        provider = default;
        if (!IsNotificationScheme(target) ||
            !target.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) ||
            !target.AbsolutePath.Equals(SignedOutPath, StringComparison.Ordinal))
        {
            return false;
        }

        string? value = HttpUtility.ParseQueryString(target.Query)["provider"];
        if (value?.Equals("bluesky", StringComparison.Ordinal) == true)
        {
            provider = WebAuthProvider.Bluesky;
            return true;
        }

        if (value?.Equals("mastodon", StringComparison.Ordinal) == true)
        {
            provider = WebAuthProvider.Mastodon;
            return true;
        }

        return false;
    }
}
