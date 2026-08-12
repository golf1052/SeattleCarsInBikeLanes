using System.Text.Json;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Views;

/// <summary>
/// Signs the user in by letting them use the real website.
/// </summary>
/// <remarks>
/// The site already implements Bluesky and Mastodon sign in, and reimplementing either in the app
/// would mean a second thing to keep correct. On iOS every WKWebView shares one cookie jar, so the
/// session established here is the same one the map tab and the app's HTTP client see.
/// </remarks>
public partial class LoginPage : ContentPage
{
    /// <summary>
    /// Reads the Mastodon credentials the site keeps in local storage.
    /// </summary>
    /// <remarks>
    /// Mastodon is the one thing cookies do not cover: the site holds that access token in the
    /// browser and sends it with each report, so the app has to read it from the same place. It is
    /// stored in the keychain and only ever sent back to this site.
    /// </remarks>
    private const string ReadMastodonScript =
        "JSON.stringify({" +
        "token: localStorage.getItem('mastodonAccessToken')," +
        "endpoint: localStorage.getItem('mastodonEndpoint')})";

    private readonly IAuthService authService;
    private readonly ILogger<LoginPage> logger;

    public LoginPage(IAuthService authService, ILogger<LoginPage> logger)
    {
        InitializeComponent();

        this.authService = authService;
        this.logger = logger;

        Web.Source = SiteUrls.Login.ToString();
    }

    /// <summary>
    /// Opens the site's own sign in dialog for a provider.
    /// </summary>
    /// <remarks>
    /// Clicking the site's button rather than reimplementing the dialog means the app never has to
    /// know how a handle is validated or where the user gets redirected. Bootstrap listens for the
    /// click on the document, so this works even though the button sits inside a collapsed navbar.
    /// </remarks>
    private async Task OpenSignInAsync(string buttonId)
    {
        try
        {
            await Web.EvaluateJavaScriptAsync(
                $"(function(){{var b=document.getElementById('{buttonId}');if(b){{b.click();return true;}}return false;}})()");
        }
        catch (Exception ex)
        {
            // The user can still reach the same buttons in the page itself.
            logger.LogWarning(ex, "Could not open the {Button} sign in dialog.", buttonId);
        }
    }

    private async void BlueskyClicked(object? sender, EventArgs e) =>
        await OpenSignInAsync("blueskySignInButton");

    private async void MastodonClicked(object? sender, EventArgs e) =>
        await OpenSignInAsync("mastodonSignInButton");

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        try
        {
            // Whatever the user did on the site, the app's idea of who they are is now stale.
            await authService.RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh the session after signing in.");
        }
    }

    private async void WebNavigated(object? sender, WebNavigatedEventArgs e)
    {
        Busy.IsRunning = false;
        Busy.IsVisible = false;

        try
        {
            await TryCaptureMastodonAsync();
            await authService.RefreshAsync();

            if (authService.CurrentIdentity?.CanAttribute == true)
            {
                await DisplayAlertAsync("Signed in",
                    $"Reports can now be credited to {authService.CurrentIdentity.DisplayName}.",
                    "OK");
                await Shell.Current.GoToAsync("..");
            }
        }
        catch (Exception ex)
        {
            // Navigation happens constantly while the user moves through the sign in flow, and a
            // failure on any single page is not worth interrupting them for.
            logger.LogDebug(ex, "Could not check the session after a navigation.");
        }
    }

    private async Task TryCaptureMastodonAsync()
    {
        string? raw = await Web.EvaluateJavaScriptAsync(ReadMastodonScript);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        try
        {
            // EvaluateJavaScriptAsync hands back the result as an escaped JSON string on iOS.
            string json = raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw) ?? raw : raw;

            MastodonSession? session = JsonSerializer.Deserialize<MastodonSession>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (session is null ||
                string.IsNullOrWhiteSpace(session.Token) ||
                string.IsNullOrWhiteSpace(session.Endpoint))
            {
                return;
            }

            AttributionIdentity? current = authService.CurrentIdentity;
            if (current?.MastodonAccessToken == session.Token)
            {
                return;
            }

            await authService.SetMastodonAsync(session.Endpoint, session.Token);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "The page did not return a Mastodon session.");
        }
    }

    private sealed record MastodonSession(string? Token, string? Endpoint);
}
