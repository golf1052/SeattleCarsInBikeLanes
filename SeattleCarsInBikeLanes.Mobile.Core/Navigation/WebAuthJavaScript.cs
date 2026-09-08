namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

/// <summary>
/// Builds calls into the embedded website's authentication bridge.
/// </summary>
public static class WebAuthJavaScript
{
    public static string Build(WebAuthAction action)
    {
        string provider = action.Provider switch
        {
            WebAuthProvider.Bluesky => "bluesky",
            WebAuthProvider.Mastodon => "mastodon",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        string method = action.Kind switch
        {
            WebAuthActionKind.OpenSignIn => "openSignIn",
            WebAuthActionKind.ApplySignedOut => "applySignedOut",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        // MAUI WebView has no typed API for calling page functions, so native auth actions cross
        // the boundary through this small, guarded JavaScript invocation.
        return $$"""
            (() => {
                const bridge = window.carsInBikeLanesMobileAuth;
                return !!bridge &&
                    typeof bridge.{{method}} === 'function' &&
                    bridge.{{method}}('{{provider}}') === true;
            })()
            """;
    }

    public static bool WasSuccessful(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return false;
        }

        string decoded = WebViewJavaScriptResult.DecodeJson(result);
        return bool.TryParse(decoded, out bool succeeded) && succeeded;
    }

}
