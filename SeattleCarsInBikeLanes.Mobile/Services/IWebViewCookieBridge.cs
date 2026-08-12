using System.Net;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Moves the sign in session from the in-app browser to the app's own HTTP client.
/// </summary>
/// <remarks>
/// The user signs in on the real website inside a WebView, so the resulting session is a cookie
/// that belongs to the web view rather than to the app. Everything the app does with the API has to
/// present that same session, and the two do not share a cookie jar on their own.
///
/// On iOS every WKWebView shares one cookie store by default, so signing in anywhere in the app
/// (including on the map tab) is enough.
/// </remarks>
public interface IWebViewCookieBridge
{
    /// <summary>
    /// Copies the site's cookies out of the web view and into <paramref name="container"/>.
    /// </summary>
    Task SyncToAsync(CookieContainer container, Uri siteUri);

    /// <summary>
    /// Clears the site's cookies from the web view, so signing out actually signs out.
    /// </summary>
    Task ClearAsync(Uri siteUri);
}

/// <summary>
/// Used on platforms with no cookie bridge implementation.
/// </summary>
public sealed class NullWebViewCookieBridge : IWebViewCookieBridge
{
    public Task SyncToAsync(CookieContainer container, Uri siteUri) => Task.CompletedTask;

    public Task ClearAsync(Uri siteUri) => Task.CompletedTask;
}
