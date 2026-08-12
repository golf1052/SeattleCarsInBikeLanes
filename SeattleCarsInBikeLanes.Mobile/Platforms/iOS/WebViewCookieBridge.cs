using System.Net;
using Foundation;
using SeattleCarsInBikeLanes.Mobile.Services;
using WebKit;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

/// <summary>
/// Copies the sign in cookie between WKWebView and the app's HTTP client.
/// </summary>
/// <remarks>
/// Every WKWebView the app creates uses <see cref="WKWebsiteDataStore.DefaultDataStore"/>, so the
/// map tab and the login page share one cookie jar and signing in on either signs in the app.
/// </remarks>
public sealed class WebViewCookieBridge : IWebViewCookieBridge
{
    public Task SyncToAsync(CookieContainer container, Uri siteUri)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(siteUri);

        TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();

        // The cookie store may only be touched from the main thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            WKWebsiteDataStore.DefaultDataStore.HttpCookieStore.GetAllCookies(cookies =>
            {
                try
                {
                    foreach (NSHttpCookie cookie in cookies)
                    {
                        if (!DomainMatches(cookie.Domain, siteUri.Host))
                        {
                            continue;
                        }

                        try
                        {
                            container.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, siteUri.Host)
                            {
                                Secure = cookie.IsSecure,
                                HttpOnly = cookie.IsHttpOnly
                            });
                        }
                        catch (CookieException)
                        {
                            // Values containing a comma or semicolon are rejected outright. Skipping
                            // just that cookie matters, because abandoning the whole loop could drop
                            // the session cookie and silently sign the user out.
                        }
                    }
                }
                finally
                {
                    completion.TrySetResult(true);
                }
            });
        });

        return completion.Task;
    }

    public Task ClearAsync(Uri siteUri)
    {
        ArgumentNullException.ThrowIfNull(siteUri);

        TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            WKHttpCookieStore store = WKWebsiteDataStore.DefaultDataStore.HttpCookieStore;
            store.GetAllCookies(cookies =>
            {
                NSHttpCookie[] toDelete = cookies
                    .Where(cookie => DomainMatches(cookie.Domain, siteUri.Host))
                    .ToArray();

                if (toDelete.Length == 0)
                {
                    completion.TrySetResult(true);
                    return;
                }

                int remaining = toDelete.Length;
                foreach (NSHttpCookie cookie in toDelete)
                {
                    store.DeleteCookie(cookie, () =>
                    {
                        if (Interlocked.Decrement(ref remaining) == 0)
                        {
                            completion.TrySetResult(true);
                        }
                    });
                }
            });
        });

        return completion.Task;
    }

    /// <summary>
    /// Whether a cookie's domain covers the site.
    /// </summary>
    /// <remarks>
    /// Cookie domains are often stored with a leading dot to mean "and all subdomains".
    /// </remarks>
    private static bool DomainMatches(string? cookieDomain, string host)
    {
        if (string.IsNullOrWhiteSpace(cookieDomain))
        {
            return false;
        }

        string normalized = cookieDomain.TrimStart('.');
        return host.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase);
    }
}
