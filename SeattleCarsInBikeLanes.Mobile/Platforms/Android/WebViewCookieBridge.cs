using System.Net;
using Android.Webkit;
using Microsoft.Net.Http.Headers;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

/// <summary>
/// Copies the sign-in cookies between Android's WebView cookie jar and the app's HTTP client.
/// This is only used for Bluesky sign-in for validating user attribution.
/// </summary>
public sealed class WebViewCookieBridge : IWebViewCookieBridge
{
    public Task CopyWebViewCookiesToAppAsync(CookieContainer container, Uri siteUri)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(siteUri);

        TaskCompletionSource completion =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                string? header = CookieManager.Instance?.GetCookie(siteUri.AbsoluteUri);
                IList<CookieHeaderValue> cookies = string.IsNullOrWhiteSpace(header)
                    ? []
                    : CookieHeaderValue.ParseList([header]);

                foreach (CookieHeaderValue cookie in cookies)
                {
                    try
                    {
                        container.Add(new Cookie(
                            cookie.Name.ToString(),
                            cookie.Value.ToString(),
                            "/",
                            siteUri.Host)
                        {
                            Secure = siteUri.Scheme.Equals(Uri.UriSchemeHttps,
                                StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    catch (CookieException)
                    {
                        // One malformed WebView cookie must not prevent the session cookie from
                        // reaching the HTTP client.
                    }
                }

                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        return completion.Task;
    }

    public Task ClearAsync(Uri siteUri)
    {
        ArgumentNullException.ThrowIfNull(siteUri);

        TaskCompletionSource completion =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                CookieManager? manager = CookieManager.Instance;
                if (manager is null)
                {
                    completion.TrySetResult();
                    return;
                }

                string url = siteUri.AbsoluteUri;
                string? header = manager.GetCookie(url);
                IList<CookieHeaderValue> cookies = string.IsNullOrWhiteSpace(header)
                    ? []
                    : CookieHeaderValue.ParseList([header]);

                foreach (string name in cookies
                    .Select(cookie => cookie.Name.ToString())
                    .Distinct(StringComparer.Ordinal))
                {
                    string expired =
                        $"{name}=; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/";

                    // Android exposes neither cookie domains nor selective deletion. Expire both
                    // possible keys for this host rather than using RemoveAllCookies, which would
                    // also sign the user out of unrelated sites.
                    manager.SetCookie(url, expired);
                    manager.SetCookie(url, $"{expired}; Domain={siteUri.Host}");
                }

                manager.Flush();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        return completion.Task;
    }
}
