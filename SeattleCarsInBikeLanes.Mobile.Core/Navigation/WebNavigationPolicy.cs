namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

public enum WebNavigationAction
{
    StayInWebView,
    OpenExternally,
    RestartSocialAuthorization
}

public sealed class WebNavigationPolicy
{
    private const string OAuthRequestUriPrefix = "urn:ietf:params:oauth:request_uri:";

    private bool socialAuthorizationInProgress;

    public void ResetSocialAuthorization() => socialAuthorizationInProgress = false;

    public WebNavigationAction GetAction(Uri target, Uri siteBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(siteBaseAddress);

        if (target.Host.Equals(siteBaseAddress.Host, StringComparison.OrdinalIgnoreCase))
        {
            socialAuthorizationInProgress = false;
            return WebNavigationAction.StayInWebView;
        }

        if (socialAuthorizationInProgress)
        {
            return WebNavigationAction.StayInWebView;
        }

        socialAuthorizationInProgress =
            IsBlueskyAuthorization(target, siteBaseAddress) ||
            IsMastodonAuthorization(target, siteBaseAddress);

        return socialAuthorizationInProgress
            ? WebNavigationAction.RestartSocialAuthorization
            : WebNavigationAction.OpenExternally;
    }

    private static bool IsBlueskyAuthorization(Uri target, Uri siteBaseAddress)
    {
        if (!target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Dictionary<string, string> query = ParseQuery(target.Query);
        string expectedClientId = new Uri(siteBaseAddress, "client-metadata.json").AbsoluteUri;

        return query.TryGetValue("client_id", out string? clientId) &&
            clientId.Equals(expectedClientId, StringComparison.Ordinal) &&
            query.TryGetValue("request_uri", out string? requestUri) &&
            requestUri.StartsWith(OAuthRequestUriPrefix, StringComparison.Ordinal);
    }

    private static bool IsMastodonAuthorization(Uri target, Uri siteBaseAddress)
    {
        if (!target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Dictionary<string, string> query = ParseQuery(target.Query);
        string expectedRedirectUri = new Uri(siteBaseAddress, "mastodonredirect").AbsoluteUri;

        return query.TryGetValue("response_type", out string? responseType) &&
            responseType.Equals("code", StringComparison.Ordinal) &&
            query.TryGetValue("client_id", out string? clientId) &&
            !string.IsNullOrWhiteSpace(clientId) &&
            query.TryGetValue("redirect_uri", out string? redirectUri) &&
            redirectUri.Equals(expectedRedirectUri, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..separator].Replace('+', ' '));
            string value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            values[key] = value;
        }

        return values;
    }
}
