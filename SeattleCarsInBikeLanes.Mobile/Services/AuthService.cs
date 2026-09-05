using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Who the user is signed in as, for attribution.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// The identity the app last saw, without going to the network.
    /// </summary>
    AttributionIdentity? CurrentIdentity { get; }

    event EventHandler? IdentityChanged;

    /// <summary>
    /// Loads whatever was stored last time the app ran.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Re-reads the session from the web view and the site.
    /// </summary>
    Task<AttributionIdentity?> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the Mastodon credentials the site left in the browser.
    /// </summary>
    /// <remarks>
    /// The username is not stored alongside them, so it is resolved from the server the same way
    /// the website does.
    /// </remarks>
    Task SetMastodonAsync(string endpoint, string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out of Bluesky only, leaving a linked Mastodon account signed in.
    /// </summary>
    Task SignOutBlueskyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out of Mastodon only, leaving a linked Bluesky account signed in.
    /// </summary>
    /// <remarks>
    /// The server keeps no Mastodon session of its own, so this never needs a network call: the
    /// access token lives only in secure storage and forgetting it is enough.
    /// </remarks>
    Task SignOutMastodonAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies whatever credentials the app holds to an outgoing request.
    /// </summary>
    Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AuthService : IAuthService
{
    private const string BlueskyTokenKey = "cbl.bluesky-token";
    private const string BlueskyHandleKey = "cbl.bluesky-handle";
    private const string MastodonTokenKey = "cbl.mastodon-token";
    private const string MastodonEndpointKey = "cbl.mastodon-endpoint";
    private const string MastodonUsernameKey = "cbl.mastodon-username";
    private const string MastodonFullUsernameKey = "cbl.mastodon-full-username";

    private readonly HttpClient httpClient;
    private readonly CookieContainer cookieContainer;
    private readonly IWebViewCookieBridge cookieBridge;
    private readonly ILogger<AuthService> logger;
    private readonly SemaphoreSlim initializeMutex = new SemaphoreSlim(1, 1);

    private string? blueskyToken;
    private bool initialized;

    public AuthService(HttpClient httpClient,
        CookieContainer cookieContainer,
        IWebViewCookieBridge cookieBridge,
        ILogger<AuthService> logger)
    {
        this.httpClient = httpClient;
        this.cookieContainer = cookieContainer;
        this.cookieBridge = cookieBridge;
        this.logger = logger;
    }

    public AttributionIdentity? CurrentIdentity { get; private set; }

    public event EventHandler? IdentityChanged;

    /// <summary>
    /// Loads whatever was stored last time the app ran.
    /// </summary>
    /// <remarks>
    /// Run at startup rather than from a page, because a report can be sent from the queue without
    /// the user having opened anything that would otherwise refresh the identity, and a report that
    /// was meant to be credited to somebody would go up anonymous.
    /// </remarks>
    public async Task InitializeAsync()
    {
        await initializeMutex.WaitAsync();
        try
        {
            if (initialized)
            {
                return;
            }

            blueskyToken = await TryGetAsync(BlueskyTokenKey);
            CurrentIdentity = await BuildIdentityAsync(await TryGetAsync(BlueskyHandleKey));
            initialized = true;
            RaiseIdentityChanged();
        }
        finally
        {
            initializeMutex.Release();
        }
    }

    public async Task<AttributionIdentity?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Pick up a session the user may have just established in the map or login web view.
        await cookieBridge.CopyWebViewCookiesToAppAsync(cookieContainer, SiteUrls.BaseAddress);

        string? handle = await GetBlueskyHandleAsync(cancellationToken);

        if (handle is not null)
        {
            await TrySetAsync(BlueskyHandleKey, handle);

            // A bearer token outlives the web view's cookie, so the app keeps working after iOS
            // evicts web site data.
            if (blueskyToken is null)
            {
                await TryExchangeForTokenAsync(cancellationToken);
            }
        }
        else if (blueskyToken is not null)
        {
            // The stored token is no longer accepted.
            blueskyToken = null;
            SecureStorage.Default.Remove(BlueskyTokenKey);
            SecureStorage.Default.Remove(BlueskyHandleKey);
        }

        CurrentIdentity = await BuildIdentityAsync(handle);
        RaiseIdentityChanged();
        return CurrentIdentity;
    }

    public async Task SetMastodonAsync(string endpoint,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        MastodonUsername? username = await ResolveMastodonUsernameAsync(endpoint, accessToken, cancellationToken);
        if (username is null)
        {
            // Without a verified username the server would refuse to credit the report anyway, so
            // storing the token would only produce a sign in that silently does nothing.
            logger.LogWarning("Could not resolve the Mastodon username, so the account was not linked.");
            return;
        }

        await TrySetAsync(MastodonEndpointKey, endpoint);
        await TrySetAsync(MastodonUsernameKey, username.Username);
        await TrySetAsync(MastodonFullUsernameKey, username.FullUsername);
        await TrySetAsync(MastodonTokenKey, accessToken);
        CurrentIdentity = await BuildIdentityAsync(CurrentIdentity?.BlueskyHandle);
        RaiseIdentityChanged();
    }

    private async Task<MastodonUsername?> ResolveMastodonUsernameAsync(string endpoint,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(SiteUrls.MastodonUsername,
                new { accessToken, serverUrl = endpoint },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            MastodonUsername? username =
                await response.Content.ReadFromJsonAsync<MastodonUsername>(cancellationToken);

            return string.IsNullOrWhiteSpace(username?.FullUsername) ? null : username;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve the Mastodon username.");
            return null;
        }
    }

    public async Task SignOutBlueskyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, SiteUrls.BlueskyLogout);
            await AuthenticateAsync(request, cancellationToken);
            await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // The local session is going away regardless, so a failure to tell the server is not
            // worth blocking sign out over.
            logger.LogWarning(ex, "Failed to tell the server about signing out of Bluesky.");
        }

        foreach (string key in new[] { BlueskyTokenKey, BlueskyHandleKey })
        {
            SecureStorage.Default.Remove(key);
        }

        blueskyToken = null;
        ClearCookies();
        await cookieBridge.ClearAsync(SiteUrls.BaseAddress);

        // A linked Mastodon account is unaffected: it carries its own credentials and has no
        // session of its own to sign out of here.
        CurrentIdentity = await BuildIdentityAsync(null);
        RaiseIdentityChanged();
    }

    public async Task SignOutMastodonAsync(CancellationToken cancellationToken = default)
    {
        foreach (string key in new[]
        {
            MastodonTokenKey, MastodonEndpointKey, MastodonUsernameKey, MastodonFullUsernameKey
        })
        {
            SecureStorage.Default.Remove(key);
        }

        // A linked Bluesky account is unaffected: its cookie/token and secure storage are untouched.
        CurrentIdentity = await BuildIdentityAsync(CurrentIdentity?.BlueskyHandle);
        RaiseIdentityChanged();
    }

    public async Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (blueskyToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", blueskyToken);
            return;
        }

        // With no token the cookie is the only thing identifying the user, and it may have been
        // created in the web view since the last request.
        await cookieBridge.CopyWebViewCookiesToAppAsync(cookieContainer, SiteUrls.BaseAddress);
    }

    private async Task<string?> GetBlueskyHandleAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, SiteUrls.BlueskyMe);
            if (blueskyToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", blueskyToken);
            }

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            MeResponse? me = await response.Content.ReadFromJsonAsync<MeResponse>(cancellationToken);
            return me is { LoggedIn: true } && !string.IsNullOrWhiteSpace(me.Handle) ? me.Handle : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check the Bluesky session.");
            return null;
        }
    }

    private async Task TryExchangeForTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(SiteUrls.BlueskyToken, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            TokenResponse? token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (token is null || string.IsNullOrWhiteSpace(token.Token))
            {
                return;
            }

            blueskyToken = token.Token;
            await TrySetAsync(BlueskyTokenKey, token.Token);
        }
        catch (Exception ex)
        {
            // Without a token the cookie still works, so this is a durability problem rather than
            // a broken sign in.
            logger.LogWarning(ex, "Could not exchange the session for a bearer token.");
        }
    }

    private async Task<AttributionIdentity?> BuildIdentityAsync(string? blueskyHandle)
    {
        string? mastodonToken = await TryGetAsync(MastodonTokenKey);
        string? mastodonEndpoint = await TryGetAsync(MastodonEndpointKey);
        string? mastodonUsername = await TryGetAsync(MastodonUsernameKey);
        string? mastodonFullUsername = await TryGetAsync(MastodonFullUsernameKey);

        AttributionIdentity identity = new AttributionIdentity()
        {
            BlueskyHandle = blueskyHandle,
            MastodonAccessToken = mastodonToken,
            MastodonEndpoint = mastodonEndpoint,
            MastodonUsername = mastodonUsername,
            MastodonFullUsername = mastodonFullUsername
        };

        return identity.CanAttribute ? identity : null;
    }

    private void ClearCookies()
    {
        foreach (Cookie cookie in cookieContainer.GetCookies(SiteUrls.BaseAddress).Cast<Cookie>())
        {
            cookie.Expired = true;
        }
    }

    private void RaiseIdentityChanged() =>
        MainThread.BeginInvokeOnMainThread(() => IdentityChanged?.Invoke(this, EventArgs.Empty));

    private async Task<string?> TryGetAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read {Key} from secure storage.", key);
            return null;
        }
    }

    private async Task TrySetAsync(string key, string value)
    {
        try
        {
            await SecureStorage.Default.SetAsync(key, value);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write {Key} to secure storage.", key);
        }
    }

    private sealed record MeResponse(
        [property: JsonPropertyName("loggedIn")] bool LoggedIn,
        [property: JsonPropertyName("did")] string? Did,
        [property: JsonPropertyName("handle")] string? Handle);

    private sealed record TokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds,
        [property: JsonPropertyName("did")] string? Did,
        [property: JsonPropertyName("handle")] string? Handle);

    private sealed record MastodonUsername(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("fullUsername")] string FullUsername);
}
