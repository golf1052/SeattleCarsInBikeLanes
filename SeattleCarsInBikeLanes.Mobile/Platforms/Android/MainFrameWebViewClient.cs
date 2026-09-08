using Android.Webkit;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AndroidWebView = Android.Webkit.WebView;

namespace SeattleCarsInBikeLanes.Mobile;

/// <summary>
/// Stops a page's own iframes from being reported as the user following a link.
/// </summary>
/// <remarks>
/// The Android counterpart to the iOS navigation delegate: <c>ShouldOverrideUrlLoading</c> is called
/// for subframes as well, so the map's embedded posts would otherwise be treated as off-site links
/// and opened in the system browser.
/// </remarks>
public sealed class MainFrameWebViewClient : MauiWebViewClient
{
    public MainFrameWebViewClient(WebViewHandler handler) : base(handler)
    {
    }

    public override bool ShouldOverrideUrlLoading(AndroidWebView? view, IWebResourceRequest? request)
    {
        if (request is { IsForMainFrame: false })
        {
            // Returning false lets the web view load the subframe itself.
            return false;
        }

        return base.ShouldOverrideUrlLoading(view, request);
    }
}
