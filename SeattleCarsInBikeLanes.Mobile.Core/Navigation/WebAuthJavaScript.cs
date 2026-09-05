namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

/// <summary>
/// Builds version-tolerant calls into the embedded website's authentication UI.
/// </summary>
public static class WebAuthJavaScript
{
    public static string EnableNativeNotifications =>
        "(function(){" +
        "if(window.__carsInBikeLanesNativeAuthEnabled){return true;}" +
        "window.__carsInBikeLanesNativeAuthEnabled=true;" +
        "var bridge=window.carsInBikeLanesMobileAuth;" +
        "if(bridge&&typeof bridge.enableNativeNotifications==='function'){" +
        "return bridge.enableNativeNotifications()===true;}" +
        "var pending={};" +
        "function notify(provider){" +
        "delete pending[provider];" +
        "window.location.href='cibl-mobile://auth/signed-out?provider='+provider;}" +
        "function schedule(provider){" +
        "if(pending[provider]){clearTimeout(pending[provider]);}" +
        "pending[provider]=setTimeout(function(){notify(provider);},500);}" +
        "window.addEventListener('carsInBikeLanesAuthChanged',function(event){" +
        "var detail=event.detail;var provider=detail&&detail.provider;" +
        "if(detail&&detail.signedIn===false&&(provider==='bluesky'||provider==='mastodon')){" +
        "if(pending[provider]){clearTimeout(pending[provider]);}" +
        "notify(provider);}" +
        "});" +
        "[['bluesky','blueskyLogoutButton'],['mastodon','mastodonLogoutButton']]" +
        ".forEach(function(item){var button=document.getElementById(item[1]);" +
        "if(button){button.addEventListener('click',function(){schedule(item[0]);});}});" +
        "return true;" +
        "})()";

    public static string Build(WebAuthAction action)
    {
        string provider = action.Provider switch
        {
            WebAuthProvider.Bluesky => "bluesky",
            WebAuthProvider.Mastodon => "mastodon",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        return action.Kind switch
        {
            WebAuthActionKind.OpenSignIn => BuildOpenSignIn(provider),
            WebAuthActionKind.ApplySignedOut => BuildApplySignedOut(provider),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
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

    private static string BuildOpenSignIn(string provider)
    {
        string modalId = provider == "bluesky" ? "blueskyHandleModal" : "mastodonServerModal";
        string buttonId = provider == "bluesky" ? "blueskySignInButton" : "mastodonSignInButton";

        return
            "(function(){" +
            "var bridge=window.carsInBikeLanesMobileAuth;" +
            $"if(bridge&&typeof bridge.openSignIn==='function'){{return bridge.openSignIn('{provider}')===true;}}" +
            $"var modal=document.getElementById('{modalId}');" +
            "if(modal&&window.bootstrap&&bootstrap.Modal){" +
            "bootstrap.Modal.getOrCreateInstance(modal).show();return true;}" +
            $"var button=document.getElementById('{buttonId}');" +
            "if(button){button.click();return true;}return false;" +
            "})()";
    }

    private static string BuildApplySignedOut(string provider)
    {
        string fallback = provider == "bluesky"
            ? "if(typeof setBlueskyLoggedOut==='function'){setBlueskyLoggedOut();return true;}" +
                "window.blueskyHandle=null;window.blueskyUserDid=null;" +
                "var signIn=document.getElementById('blueskySignInButton');" +
                "var signOut=document.getElementById('blueskyLogoutButton');" +
                "if(signIn){signIn.removeAttribute('disabled');signIn.innerText='Sign in with Bluesky';}" +
                "if(signOut){signOut.className='dropdown-item disabled';}" +
                "return !!(signIn||signOut);"
            : "if(typeof clearMastodonAuth==='function'){clearMastodonAuth(false);return true;}" +
                "localStorage.removeItem('mastodonEndpoint');" +
                "localStorage.removeItem('mastodonAccessToken');" +
                "var signIn=document.getElementById('mastodonSignInButton');" +
                "var signOut=document.getElementById('mastodonLogoutButton');" +
                "if(signIn){signIn.removeAttribute('disabled');signIn.innerText='Sign in with Mastodon';}" +
                "if(signOut){signOut.className='dropdown-item disabled';}" +
                "return !!(signIn||signOut);";

        return
            "(function(){" +
            "var bridge=window.carsInBikeLanesMobileAuth;" +
            $"if(bridge&&typeof bridge.applySignedOut==='function'){{return bridge.applySignedOut('{provider}')===true;}}" +
            fallback +
            "})()";
    }
}
