using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Views;

/// <summary>
/// The site's map, embedded.
/// </summary>
public partial class MapPage : ContentPage
{
    private readonly IAuthService authService;
    private readonly ILogger<MapPage> logger;

    public MapPage(IAuthService authService, ILogger<MapPage> logger)
    {
        InitializeComponent();

        this.authService = authService;
        this.logger = logger;

        Web.Source = SiteUrls.Map.ToString();
    }

    private void WebNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out Uri? target))
        {
            return;
        }

        // Links that lead off the site belong in the user's browser, not trapped in a tab with no
        // address bar and no way back.
        if (!target.Host.Equals(SiteUrls.BaseAddress.Host, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            Browser.Default.OpenAsync(target, BrowserLaunchMode.SystemPreferred);
            return;
        }

        Busy.IsVisible = true;
        Busy.IsRunning = true;
    }

    private async void WebNavigated(object? sender, WebNavigatedEventArgs e)
    {
        Busy.IsRunning = false;
        Busy.IsVisible = false;

        try
        {
            // The user may have signed in here rather than from Settings, and the web view's
            // cookie jar is shared with the rest of the app.
            await authService.RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not refresh the session from the map page.");
        }
    }
}
