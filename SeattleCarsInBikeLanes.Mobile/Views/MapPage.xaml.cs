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
    private readonly MapLoadRecovery mapLoadRecovery;
    private Uri? currentDocumentUri;
    private Window? observedWindow;
    private bool isPageActive;
    private bool siteNavigationInProgress = true;
    private bool socialAuthorizationInProgress;

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
        mapLoadRecovery = new MapLoadRecovery(Connectivity.Current.NetworkAccess != NetworkAccess.Internet);

        webAuthActions.PendingActionsChanged += WebAuthActionsPendingActionsChanged;
        Web.Source = SiteUrls.Map.ToString();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        isPageActive = true;
        Connectivity.Current.ConnectivityChanged += ConnectivityChanged;
        observedWindow = Window;
        if (observedWindow is not null)
        {
            observedWindow.Resumed += WindowResumed;
        }

        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            mapLoadRecovery.ConnectivityLost();
            ShowMapUnavailable();
        }
        TryRecoverMap();

        if (IsSiteUri(currentDocumentUri))
        {
            await ProcessPendingWebAuthActionsAsync();
        }
    }

    protected override void OnDisappearing()
    {
        isPageActive = false;
        Connectivity.Current.ConnectivityChanged -= ConnectivityChanged;
        if (observedWindow is not null)
        {
            observedWindow.Resumed -= WindowResumed;
            observedWindow = null;
        }

        base.OnDisappearing();
    }

    private void ConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
            {
                TryRecoverMap();
            }
            else
            {
                mapLoadRecovery.ConnectivityLost();
                ShowMapUnavailable();
            }
        });

    private void WindowResumed(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                mapLoadRecovery.ConnectivityLost();
                ShowMapUnavailable();
            }
            TryRecoverMap();
        });

    private void ShowMapUnavailable()
    {
        if (!mapLoadRecovery.NeedsRetry || socialAuthorizationInProgress)
        {
            return;
        }

        Busy.IsRunning = false;
        Busy.IsVisible = false;
        MapUnavailable.IsVisible = true;
    }

    private void TryRecoverMap()
    {
        if (!mapLoadRecovery.TryBeginRetry(
            Connectivity.Current.NetworkAccess == NetworkAccess.Internet,
            isPageActive,
            socialAuthorizationInProgress))
        {
            return;
        }

        siteNavigationInProgress = true;
        currentDocumentUri = null;
        CancelSignInButton.IsVisible = false;
        MapUnavailable.IsVisible = false;
        Busy.IsVisible = true;
        Busy.IsRunning = true;

        // A new source navigates to the site even when the failed URL is still the current source.
        Web.Source = new UrlWebViewSource { Url = SiteUrls.Map.AbsoluteUri };
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
            else if (IsSiteUri(currentDocumentUri) &&
                WebAuthNotification.TryGetSignInProvider(target, out provider))
            {
                try
                {
                    await authService.BeginSignInAsync(provider);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not begin native sign-in for {Provider}.", provider);
                    await DisplayAlertAsync("Sign in incomplete",
                        "The app could not update its saved account state. Try again.", "OK");
                }
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
            socialAuthorizationInProgress = true;
            siteNavigationInProgress = false;
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
                socialAuthorizationInProgress = false;
                logger.LogError(ex, "Could not clear the social provider session before signing in.");
                await DisplayAlertAsync("Sign in failed",
                    "The previous social account session could not be cleared. Try again.",
                    "OK");
            }

            return;
        }

        if (IsSiteUri(target) && !socialAuthorizationInProgress)
        {
            siteNavigationInProgress = true;
            mapLoadRecovery.NavigationStarted(Connectivity.Current.NetworkAccess == NetworkAccess.Internet);
        }

        if (mapLoadRecovery.NeedsRetry && Connectivity.Current.NetworkAccess != NetworkAccess.Internet &&
            !socialAuthorizationInProgress)
        {
            ShowMapUnavailable();
            return;
        }

        MapUnavailable.IsVisible = false;
        Busy.IsVisible = true;
        Busy.IsRunning = true;
    }

    private async void WebNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result == WebNavigationResult.Cancel)
        {
            return;
        }

        if (e.Result != WebNavigationResult.Success &&
            !siteNavigationInProgress && !socialAuthorizationInProgress)
        {
            return;
        }

        Busy.IsRunning = false;
        Busy.IsVisible = false;

        if (e.Result != WebNavigationResult.Success)
        {
            currentDocumentUri = null;
            CancelSignInButton.IsVisible = socialAuthorizationInProgress;
            if (siteNavigationInProgress && !socialAuthorizationInProgress)
            {
                mapLoadRecovery.NavigationFailed();
                ShowMapUnavailable();
            }

            siteNavigationInProgress = false;
            return;
        }

        currentDocumentUri = Uri.TryCreate(e.Url, UriKind.Absolute, out Uri? target) ? target : null;
        CancelSignInButton.IsVisible = currentDocumentUri is not null && !IsSiteUri(currentDocumentUri);
        if (!IsSiteUri(currentDocumentUri))
        {
            return;
        }

        siteNavigationInProgress = false;
        socialAuthorizationInProgress = false;
        mapLoadRecovery.NavigationSucceeded(Connectivity.Current.NetworkAccess == NetworkAccess.Internet);
        MapUnavailable.IsVisible = false;

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
                        if (action.Kind == WebAuthActionKind.OpenSignIn &&
                            !await authService.BeginSignInAsync(action.Provider)) return;
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

                    bool acknowledged;
                    try
                    {
                        acknowledged = action.Kind == WebAuthActionKind.ApplySignedOut
                            ? await authService.AcknowledgeWebSignOutAsync(action)
                            : webAuthActions.Acknowledge(action.Id);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "Browser sign-out acknowledgement remains pending.");
                        return;
                    }
                    if (!acknowledged)
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
        socialAuthorizationInProgress = false;
        siteNavigationInProgress = true;
        mapLoadRecovery.NavigationStarted(Connectivity.Current.NetworkAccess == NetworkAccess.Internet);
        CancelSignInButton.IsVisible = false;
        currentDocumentUri = null;
        Web.Source = new UrlWebViewSource { Url = SiteUrls.Map.AbsoluteUri };
    }

    private static bool IsSiteUri(Uri? target) =>
        target is not null &&
        target.Scheme.Equals(SiteUrls.BaseAddress.Scheme, StringComparison.OrdinalIgnoreCase) &&
        target.Host.Equals(SiteUrls.BaseAddress.Host, StringComparison.OrdinalIgnoreCase) &&
        target.Port == SiteUrls.BaseAddress.Port;
}
