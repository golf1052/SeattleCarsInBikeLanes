using System.Collections.Specialized;
using System.Web;

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

        NameValueCollection query = HttpUtility.ParseQueryString(target.Query);
        string expectedClientId = new Uri(siteBaseAddress, "client-metadata.json").AbsoluteUri;

        return query["client_id"] is string clientId &&
            clientId.Equals(expectedClientId, StringComparison.Ordinal) &&
            query["request_uri"] is string requestUri &&
            requestUri.StartsWith(OAuthRequestUriPrefix, StringComparison.Ordinal);
    }

    private static bool IsMastodonAuthorization(Uri target, Uri siteBaseAddress)
    {
        if (!target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        NameValueCollection query = HttpUtility.ParseQueryString(target.Query);
        string expectedRedirectUri = new Uri(siteBaseAddress, "mastodonredirect").AbsoluteUri;

        return query["response_type"] is string responseType &&
            responseType.Equals("code", StringComparison.Ordinal) &&
            query["client_id"] is string clientId &&
            !string.IsNullOrWhiteSpace(clientId) &&
            query["redirect_uri"] is string redirectUri &&
            redirectUri.Equals(expectedRedirectUri, StringComparison.Ordinal);
    }
}
