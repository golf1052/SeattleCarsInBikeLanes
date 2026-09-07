using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Mobile.Core.Navigation;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Services;

public interface IAuthService
{
    AttributionIdentity? CurrentIdentity { get; }
    long Generation { get; }
    string? RefreshError { get; }
    event EventHandler? IdentityChanged;
    Task InitializeAsync();
    Task<AttributionIdentity?> RefreshAsync(CancellationToken cancellationToken = default);
    Task SetMastodonAsync(string endpoint, string accessToken, CancellationToken cancellationToken = default,
        long? expectedGeneration = null);
    Task SignOutBlueskyAsync(CancellationToken cancellationToken = default);
    Task SignOutMastodonAsync(CancellationToken cancellationToken = default);
    Task<bool> AcknowledgeWebSignOutAsync(WebAuthAction action);
    Task<bool> BeginSignInAsync(WebAuthProvider provider);
    Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
    Task CaptureQueuedAsync(string reportId, bool attribute, long generation,
        Func<QueuedAttribution, Task> persist, CancellationToken cancellationToken);
}

public sealed class AuthService : IAuthService
{
    private const string SessionKey = "cbl.active-session.v2";
    private readonly HttpClient cookies;
    private readonly HttpClient native;
    private readonly CookieContainer cookieContainer;
    private readonly IWebViewCookieBridge cookieBridge;
    private readonly ISecureValueStore storage;
    private readonly QueuedCredentialVault vault;
    private readonly WebAuthActionCoordinator webActions;
    private readonly IClientDispatcher dispatcher;
    private readonly ILogger<AuthService> logger;
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    private AccountSession session = new AccountSession();
    private bool initialized;
    private long generation;

    public AuthService(HttpClient cookies, HttpClient native, CookieContainer cookieContainer,
        IWebViewCookieBridge cookieBridge, ISecureValueStore storage, QueuedCredentialVault vault,
        WebAuthActionCoordinator webActions, IClientDispatcher dispatcher, ILogger<AuthService> logger)
    {
        this.cookies = cookies; this.native = native; this.cookieContainer = cookieContainer;
        this.cookieBridge = cookieBridge; this.storage = storage; this.vault = vault;
        this.webActions = webActions; this.dispatcher = dispatcher; this.logger = logger;
    }

    public AttributionIdentity? CurrentIdentity { get; private set; }
    public long Generation => Interlocked.Read(ref generation);
    public string? RefreshError { get; private set; }
    public event EventHandler? IdentityChanged;

    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (initialized) return;
            string? stored = await storage.GetAsync(SessionKey);
            AccountSession restored = stored is null ? new AccountSession() :
                JsonSerializer.Deserialize<AccountSession>(stored)
                    ?? throw new InvalidDataException("The active session is unreadable.");
            if (SignedOut(WebAuthProvider.Bluesky)) restored = restored with { Bluesky = null, BlueskySignedOut = true };
            if (SignedOut(WebAuthProvider.Mastodon)) restored = restored with { Mastodon = null, MastodonSignedOut = true };
            await SaveAsync(restored);
            initialized = true;
        }
        finally { gate.Release(); }
    }

    public async Task<AttributionIdentity?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        AccountSession original;
        long expected;
        await gate.WaitAsync(cancellationToken);
        try { original = session; expected = Generation; }
        finally { gate.Release(); }
        if (SignedOut(WebAuthProvider.Bluesky)) return CurrentIdentity;

        CredentialCheck check = original.Bluesky is null
            ? new CredentialCheck(CredentialCheckState.Invalid)
            : await CheckBlueskyAsync(original.Bluesky, cancellationToken);
        if (check.State == CredentialCheckState.Invalid)
        {
            check = await ExchangeCookieAsync(original.Bluesky?.AccountId, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (expected != Generation || SignedOut(WebAuthProvider.Bluesky)) return CurrentIdentity;
            if (check.State == CredentialCheckState.Unavailable)
            {
                logger.LogDebug("Account refresh is unavailable; preserving the saved identity.");
                RefreshError = "Couldn't refresh the account. Your saved sign-in has been kept.";
                RaiseChanged();
                return CurrentIdentity;
            }
            RefreshError = null;
            await SaveAsync(session with { Bluesky = check.Credential });
            return CurrentIdentity;
        }
        finally { gate.Release(); }
    }

    private async Task<CredentialCheck> CheckBlueskyAsync(AccountCredential credential, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, SiteUrls.BlueskyNativeMe);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            using HttpResponseMessage response = await native.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(CredentialCheckState.Invalid);
            if (!response.IsSuccessStatusCode) return new(CredentialCheckState.Unavailable);
            CredentialIdentity? identity = await response.Content.ReadFromJsonAsync<CredentialIdentity>(token);
            if (identity is null || identity.AccountId != credential.AccountId || string.IsNullOrWhiteSpace(identity.DisplayName))
                return new(CredentialCheckState.Unavailable);
            return new(CredentialCheckState.Valid,
                credential with { DisplayName = identity.DisplayName, ExpiresAt = identity.ExpiresAt });
        }
        catch (Exception ex) when (IsUnavailable(ex, token))
        {
            return new(CredentialCheckState.Unavailable);
        }
    }

    private async Task<CredentialCheck> ExchangeCookieAsync(string? expectedDid, CancellationToken token)
    {
        try
        {
            await cookieBridge.CopyWebViewCookiesToAppAsync(cookieContainer, SiteUrls.BaseAddress);
            token.ThrowIfCancellationRequested();
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, SiteUrls.BlueskyToken);
            using HttpResponseMessage response = await cookies.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(CredentialCheckState.Invalid);
            if (!response.IsSuccessStatusCode) return new(CredentialCheckState.Unavailable);
            TokenResponse? exchanged = await response.Content.ReadFromJsonAsync<TokenResponse>(token);
            if (string.IsNullOrWhiteSpace(exchanged?.Token) || string.IsNullOrWhiteSpace(exchanged.Did) ||
                string.IsNullOrWhiteSpace(exchanged.Handle) || exchanged.ExpiresInSeconds <= 0)
                return new(CredentialCheckState.Unavailable);
            if (expectedDid is not null && expectedDid != exchanged.Did)
                return new(CredentialCheckState.Invalid);
            return new(CredentialCheckState.Valid, new AccountCredential(exchanged.Did, exchanged.Handle,
                exchanged.Token, ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(exchanged.ExpiresInSeconds)));
        }
        catch (Exception ex) when (IsUnavailable(ex, token))
        {
            return new(CredentialCheckState.Unavailable);
        }
    }

    public async Task SetMastodonAsync(string endpoint, string accessToken, CancellationToken cancellationToken = default,
        long? expectedGeneration = null)
    {
        await InitializeAsync();
        long expected;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (SignedOut(WebAuthProvider.Mastodon) ||
                expectedGeneration.HasValue && expectedGeneration.Value != Generation) return;
            expected = Interlocked.Increment(ref generation);
        }
        finally { gate.Release(); }
        using HttpResponseMessage response = await native.PostAsJsonAsync(SiteUrls.MastodonNativeIdentity,
            new { serverUrl = endpoint, accessToken }, cancellationToken);
        response.EnsureSuccessStatusCode();
        MastodonResponse identity = await response.Content.ReadFromJsonAsync<MastodonResponse>(cancellationToken)
            ?? throw new InvalidDataException("The Mastodon account could not be verified.");
        if (string.IsNullOrWhiteSpace(identity.Id) || string.IsNullOrWhiteSpace(identity.Server) ||
            string.IsNullOrWhiteSpace(identity.Username))
            throw new InvalidDataException("The verified Mastodon account is incomplete.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (expected != Generation || SignedOut(WebAuthProvider.Mastodon)) return;
            await SaveAsync(session with
            {
                Mastodon = new AccountCredential(identity.Id, identity.Username,
                accessToken, identity.Server)
            });
        }
        finally { gate.Release(); }
    }

    public Task SignOutMastodonAsync(CancellationToken cancellationToken = default) =>
        SignOutAsync(WebAuthProvider.Mastodon, cancellationToken);
    public Task SignOutBlueskyAsync(CancellationToken cancellationToken = default) =>
        SignOutAsync(WebAuthProvider.Bluesky, cancellationToken);

    private async Task SignOutAsync(WebAuthProvider provider, CancellationToken token)
    {
        await InitializeAsync();
        await gate.WaitAsync(token);
        try
        {
            webActions.QueueApplySignedOut(provider);
            Interlocked.Increment(ref generation);
            await SaveAsync(provider == WebAuthProvider.Bluesky
                ? session with { Bluesky = null, BlueskySignedOut = true }
                : session with { Mastodon = null, MastodonSignedOut = true });
        }
        finally { gate.Release(); }
        if (provider == WebAuthProvider.Bluesky)
        {
            foreach (Cookie cookie in cookieContainer.GetCookies(SiteUrls.BaseAddress)) cookie.Expired = true;
            await cookieBridge.ClearAsync(SiteUrls.BaseAddress);
        }
    }

    public async Task<bool> AcknowledgeWebSignOutAsync(WebAuthAction action)
    {
        await InitializeAsync();
        await gate.WaitAsync();
        try
        {
            if (action.Kind != WebAuthActionKind.ApplySignedOut ||
                !webActions.GetPendingActions().Contains(action)) return false;
            AccountCredential? active = action.Provider == WebAuthProvider.Bluesky ? session.Bluesky : session.Mastodon;
            if (active is not null)
                throw new IOException("Native sign-out is unfinished; browser acknowledgement was retained.");
            if (action.Provider == WebAuthProvider.Bluesky)
            {
                foreach (Cookie cookie in cookieContainer.GetCookies(SiteUrls.BaseAddress)) cookie.Expired = true;
                await cookieBridge.ClearAsync(SiteUrls.BaseAddress);
            }
            return webActions.Acknowledge(action.Id);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> BeginSignInAsync(WebAuthProvider provider)
    {
        await InitializeAsync();
        await gate.WaitAsync();
        try
        {
            if (webActions.HasPending(WebAuthActionKind.ApplySignedOut, provider)) return false;
            await SaveAsync(provider == WebAuthProvider.Bluesky
                ? session with { Bluesky = null, BlueskySignedOut = false }
                : session with { Mastodon = null, MastodonSignedOut = false });
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task CaptureQueuedAsync(string reportId, bool attribute, long expected,
        Func<QueuedAttribution, Task> persist, CancellationToken cancellationToken)
    {
        if (!attribute)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await persist(new QueuedAttribution(new ReportAttribution()));
            return;
        }
        await InitializeAsync();
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (attribute && (expected != Generation || session.Attribution.IsAnonymous))
                throw new InvalidOperationException("The account changed before this report could be saved.");
            string? reference = attribute ? await vault.RetainAsync(reportId, session) : null;
            await persist(new QueuedAttribution(attribute ? session.Attribution : new ReportAttribution(), reference));
        }
        finally { gate.Release(); }
    }

    // Kept for non-queue callers. Queued uploads never use active authentication.
    public Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (session.Bluesky is { } bluesky)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bluesky.Token);
        return Task.CompletedTask;
    }

    private async Task SaveAsync(AccountSession next)
    {
        await storage.SetAsync(SessionKey, JsonSerializer.Serialize(next));
        if (session != next) Interlocked.Increment(ref generation);
        session = next;
        CurrentIdentity = next.Attribution.IsAnonymous ? null : new AttributionIdentity
        {
            BlueskyDid = next.Bluesky?.AccountId,
            BlueskyHandle = next.Bluesky?.DisplayName,
            MastodonAccountId = next.Mastodon?.AccountId,
            MastodonUsername = next.Mastodon?.DisplayName,
            MastodonEndpoint = next.Mastodon?.Server,
            MastodonAccessToken = next.Mastodon?.Token,
            MastodonFullUsername = next.Mastodon is { } mastodon
                ? $"@{mastodon.DisplayName}@{new Uri(mastodon.Server!).Host}" : null
        };
        RaiseChanged();
    }

    private bool SignedOut(WebAuthProvider provider) =>
        webActions.HasPending(WebAuthActionKind.ApplySignedOut, provider) ||
        (provider == WebAuthProvider.Bluesky ? session.BlueskySignedOut : session.MastodonSignedOut);
    private void RaiseChanged() => dispatcher.Dispatch(() => IdentityChanged?.Invoke(this, EventArgs.Empty));
    private static bool IsUnavailable(Exception ex, CancellationToken token) =>
        ex is HttpRequestException or JsonException || ex is OperationCanceledException && !token.IsCancellationRequested;
    private sealed record TokenResponse(string Token, string Did, string Handle, int ExpiresInSeconds);
    private sealed record MastodonResponse(string Id, string Server, string Username);
}
