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

        foreach (string pair in target.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0 ||
                !Uri.UnescapeDataString(pair[..separator])
                    .Equals("provider", StringComparison.Ordinal))
            {
                continue;
            }

            string value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (value.Equals("bluesky", StringComparison.Ordinal))
            {
                provider = WebAuthProvider.Bluesky;
                return true;
            }

            if (value.Equals("mastodon", StringComparison.Ordinal))
            {
                provider = WebAuthProvider.Mastodon;
                return true;
            }
        }

        return false;
    }
}
