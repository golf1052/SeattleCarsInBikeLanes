using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using WebKit;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

/// <summary>
/// Stops a page's own iframes from being reported as the user following a link.
/// </summary>
/// <remarks>
/// WKWebView asks for a navigation policy for every frame, not just the one the user is looking at,
/// and MAUI raises <c>Navigating</c> for all of them. The map embeds posts from Twitter, Bluesky and
/// Mastodon, so those embeds each look like a navigation to another host and the map's "open
/// off-site links in the browser" rule sent the user out to Safari as soon as the map loaded.
/// </remarks>
public sealed class MainFrameNavigationDelegate : MauiWebViewNavigationDelegate
{
    public MainFrameNavigationDelegate(IWebViewHandler handler) : base(handler)
    {
    }

    public override void DecidePolicy(WKWebView webView,
        WKNavigationAction navigationAction,
        Action<WKNavigationActionPolicy> decisionHandler)
    {
        // A missing target frame means one is about to be created, which is how target="_blank" and
        // window.open arrive. Those are the user opening something, so they still go to the base
        // implementation; only a load into an existing subframe is the page loading itself.
        if (navigationAction.TargetFrame is { MainFrame: false })
        {
            decisionHandler(WKNavigationActionPolicy.Allow);
            return;
        }

        base.DecidePolicy(webView, navigationAction, decisionHandler);
    }
}
