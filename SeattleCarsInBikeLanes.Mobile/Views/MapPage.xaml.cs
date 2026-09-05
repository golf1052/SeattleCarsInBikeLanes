using SeattleCarsInBikeLanes.Mobile.Core.Navigation;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Views;

/// <summary>
/// The site's map, embedded.
/// </summary>
public partial class MapPage : ContentPage
{
    private readonly IAuthService authService;
    private readonly IWebViewCookieBridge cookieBridge;
    private readonly IMastodonSessionCapture mastodonSessionCapture;
    private readonly WebAuthActionCoordinator webAuthActions;
    private readonly ILogger<MapPage> logger;
    private readonly WebNavigationPolicy navigationPolicy = new WebNavigationPolicy();
    private readonly SemaphoreSlim webAuthMutex = new SemaphoreSlim(1, 1);
    private Uri? currentDocumentUri;

    public MapPage(IAuthService authService,
        IWebViewCookieBridge cookieBridge,
        IMastodonSessionCapture mastodonSessionCapture,
        WebAuthActionCoordinator webAuthActions,
        ILogger<MapPage> logger)
    {
        InitializeComponent();

        this.authService = authService;
        this.cookieBridge = cookieBridge;
        this.mastodonSessionCapture = mastodonSessionCapture;
        this.webAuthActions = webAuthActions;
        this.logger = logger;

        webAuthActions.PendingActionsChanged += WebAuthActionsPendingActionsChanged;
        Web.Source = SiteUrls.Map.ToString();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (IsSiteUri(currentDocumentUri))
        {
            await ProcessPendingWebAuthActionsAsync();
        }
    }

    private async void WebNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out Uri? target))
        {
            return;
        }

        if (WebAuthNotification.IsNotificationScheme(target))
        {
            e.Cancel = true;
            if (IsSiteUri(currentDocumentUri) &&
                WebAuthNotification.TryGetSignedOutProvider(target, out WebAuthProvider provider))
            {
                await ApplyWebSignOutAsync(provider);
            }

            return;
        }

        WebNavigationAction action = navigationPolicy.GetAction(target, SiteUrls.BaseAddress);
        if (action == WebNavigationAction.OpenExternally)
        {
            e.Cancel = true;
            await Browser.Default.OpenAsync(target, BrowserLaunchMode.SystemPreferred);
            return;
        }

        if (action == WebNavigationAction.RestartSocialAuthorization)
        {
            e.Cancel = true;
            try
            {
                // The site's logout disconnects its token, not the provider's own browser session.
                // Drop that provider session before OAuth so another account can be entered.
                await cookieBridge.ClearAsync(new Uri(target.GetLeftPart(UriPartial.Authority)));
                Web.Source = target.ToString();
            }
            catch (Exception ex)
            {
                navigationPolicy.ResetSocialAuthorization();
                logger.LogError(ex, "Could not clear the social provider session before signing in.");
                await DisplayAlertAsync("Sign in failed",
                    "The previous social account session could not be cleared. Try again.",
                    "OK");
            }

            return;
        }

        Busy.IsVisible = true;
        Busy.IsRunning = true;
    }

    private async void WebNavigated(object? sender, WebNavigatedEventArgs e)
    {
        Busy.IsRunning = false;
        Busy.IsVisible = false;

        currentDocumentUri = Uri.TryCreate(e.Url, UriKind.Absolute, out Uri? target) ? target : null;
        CancelSignInButton.IsVisible = currentDocumentUri is not null && !IsSiteUri(currentDocumentUri);
        if (!IsSiteUri(currentDocumentUri))
        {
            return;
        }

        try
        {
            await ProcessPendingWebAuthActionsAsync();

            // The user may have signed in here rather than from Settings, and the web view's
            // cookies and local storage both need to be copied into the native auth service.
            if (!webAuthActions.HasPending(
                WebAuthActionKind.ApplySignedOut,
                WebAuthProvider.Mastodon))
            {
                await mastodonSessionCapture.CaptureAsync(Web);
            }

            await authService.RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not refresh the session from the map page.");
        }
    }

    private void WebAuthActionsPendingActionsChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (IsSiteUri(currentDocumentUri))
            {
                await ProcessPendingWebAuthActionsAsync();
            }
        });

    private async Task ProcessPendingWebAuthActionsAsync()
    {
        await webAuthMutex.WaitAsync();
        try
        {
            while (webAuthActions.GetPendingActions() is { Count: > 0 } pending)
            {
                IEnumerable<WebAuthAction> ordered = pending
                    .OrderByDescending(action => action.Kind == WebAuthActionKind.ApplySignedOut);

                foreach (WebAuthAction action in ordered)
                {
                    string? result;
                    try
                    {
                        result = await Web.EvaluateJavaScriptAsync(WebAuthJavaScript.Build(action));
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex,
                            "The map is not ready to apply the {Action} action for {Provider}.",
                            action.Kind,
                            action.Provider);
                        return;
                    }

                    if (!WebAuthJavaScript.WasSuccessful(result))
                    {
                        logger.LogDebug(
                            "The map did not apply the {Action} action for {Provider}.",
                            action.Kind,
                            action.Provider);
                        return;
                    }

                    if (!webAuthActions.Acknowledge(action.Id))
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            webAuthMutex.Release();
        }
    }

    private async Task ApplyWebSignOutAsync(WebAuthProvider provider)
    {
        try
        {
            if (provider == WebAuthProvider.Bluesky)
            {
                await authService.SignOutBlueskyAsync();
            }
            else
            {
                await authService.SignOutMastodonAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not apply the web sign out for {Provider}.", provider);
            await DisplayAlertAsync("Sign out incomplete",
                $"The website signed out of {provider}, but the app could not update Settings. Try again.",
                "OK");
        }
    }

    private void CancelSignInClicked(object? sender, EventArgs e)
    {
        navigationPolicy.ResetSocialAuthorization();
        CancelSignInButton.IsVisible = false;
        currentDocumentUri = null;
        Web.Source = SiteUrls.Map.ToString();
    }

    private static bool IsSiteUri(Uri? target) =>
        target is not null &&
        target.Scheme.Equals(SiteUrls.BaseAddress.Scheme, StringComparison.OrdinalIgnoreCase) &&
        target.Host.Equals(SiteUrls.BaseAddress.Host, StringComparison.OrdinalIgnoreCase) &&
        target.Port == SiteUrls.BaseAddress.Port;
}
