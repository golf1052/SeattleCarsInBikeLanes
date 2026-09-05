using System.Text.Json;
using SeattleCarsInBikeLanes.Mobile.Core.Navigation;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Copies the Mastodon credentials stored by the embedded website into the app.
/// </summary>
public interface IMastodonSessionCapture
{
    Task CaptureAsync(WebView webView);
}

/// <inheritdoc />
public sealed class MastodonSessionCapture : IMastodonSessionCapture
{
    /// <summary>
    /// Reads the Mastodon credentials the site keeps in local storage.
    /// </summary>
    /// <remarks>
    /// Mastodon is the one thing cookies do not cover: the site holds that access token in the
    /// browser and sends it with each report, so the app has to read it from every WebView where
    /// sign-in is available. It is stored in secure storage and only ever sent back to this site.
    /// </remarks>
    private const string ReadMastodonScript =
        "JSON.stringify({" +
        "token: localStorage.getItem('mastodonAccessToken')," +
        "endpoint: localStorage.getItem('mastodonEndpoint')})";

    private readonly IAuthService authService;
    private readonly ILogger<MastodonSessionCapture> logger;

    public MastodonSessionCapture(IAuthService authService, ILogger<MastodonSessionCapture> logger)
    {
        this.authService = authService;
        this.logger = logger;
    }

    public async Task CaptureAsync(WebView webView)
    {
        ArgumentNullException.ThrowIfNull(webView);

        string? raw = await webView.EvaluateJavaScriptAsync(ReadMastodonScript);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        try
        {
            string json = WebViewJavaScriptResult.DecodeJson(raw);

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
