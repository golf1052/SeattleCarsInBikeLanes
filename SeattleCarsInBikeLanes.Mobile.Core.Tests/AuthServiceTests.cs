using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Mobile.Core.Navigation;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class AuthServiceTests
{
    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("500")]
    [InlineData("429")]
    [InlineData("malformed")]
    public async Task TransientRefreshPreservesCredentialsAndIdentity(string failure)
    {
        AuthFixture fixture = new AuthFixture();
        fixture.Native.Response = (_, _) => failure switch
        {
            "network" => throw new HttpRequestException("offline"),
            "timeout" => throw new TaskCanceledException("timeout"),
            "malformed" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid") }),
            _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)int.Parse(failure)))
        };
        AuthService auth = fixture.Create();
        await auth.InitializeAsync();
        string before = fixture.Storage.Values["cbl.active-session.v2"];
        await auth.RefreshAsync();
        Assert.Equal("a.bsky.social", auth.CurrentIdentity?.BlueskyHandle);
        Assert.Equal(before, fixture.Storage.Values["cbl.active-session.v2"]);
        Assert.NotNull(auth.RefreshError);
    }

    [Fact]
    public async Task ValidBearerSurvivesMissingCookie()
    {
        AuthFixture fixture = new AuthFixture();
        fixture.Native.Response = (request, _) =>
        {
            Assert.Equal("token-a", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(new CredentialIdentity("did:plc:a", "a.bsky.social")));
        };
        AuthService auth = fixture.Create();
        await auth.RefreshAsync();
        Assert.Equal("did:plc:a", auth.CurrentIdentity?.BlueskyDid);
        Assert.Equal(0, fixture.Bridge.Copies);
    }

    [Fact]
    public async Task ExpiredBearerIsReplacedFromMatchingCookieWithoutSendingExpiredBearer()
    {
        AuthFixture fixture = new AuthFixture();
        fixture.Native.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        fixture.Cookies.Response = (request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Json(new { token = "renewed-a", did = "did:plc:a", handle = "a.bsky.social", expiresInSeconds = 3600 }));
        };
        AuthService auth = fixture.Create();
        await auth.RefreshAsync();
        AccountSession saved = JsonSerializer.Deserialize<AccountSession>(fixture.Storage.Values["cbl.active-session.v2"])!;
        Assert.Equal("renewed-a", saved.Bluesky?.Token);
        Assert.NotNull(saved.Bluesky?.ExpiresAt);
        Assert.Equal(1, fixture.Bridge.Copies);
    }

    [Fact]
    public async Task ExpiredBearerDoesNotAdoptAnotherAccountsCookie()
    {
        AuthFixture fixture = new AuthFixture();
        fixture.Cookies.Response = (_, _) => Task.FromResult(Json(new
        { token = "b", did = "did:plc:b", handle = "b.bsky.social", expiresInSeconds = 3600 }));
        AuthService auth = fixture.Create();
        await auth.RefreshAsync();
        Assert.Null(auth.CurrentIdentity?.BlueskyDid);
        Assert.Equal("mastodon-a", auth.CurrentIdentity?.MastodonAccountId);
    }

    [Fact]
    public async Task CallerCancellationDoesNotSignOut()
    {
        AuthFixture fixture = new AuthFixture();
        fixture.Native.Response = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        };
        AuthService auth = fixture.Create();
        await auth.InitializeAsync();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task refresh = auth.RefreshAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal("did:plc:a", auth.CurrentIdentity?.BlueskyDid);
    }

    [Fact]
    public async Task LateRefreshCannotUndoSignOut()
    {
        AuthFixture fixture = new AuthFixture();
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Native.Response = (_, _) => response.Task;
        AuthService auth = fixture.Create();
        Task refresh = auth.RefreshAsync();
        await auth.SignOutBlueskyAsync();
        response.SetResult(Json(new CredentialIdentity("did:plc:a", "a.bsky.social")));
        await refresh;
        Assert.Null(auth.CurrentIdentity?.BlueskyDid);
        Assert.Equal("mastodon-a", auth.CurrentIdentity?.MastodonAccountId);
    }

    [Fact]
    public async Task SignOutSurvivesRecreationAndRetainsQueuedCredentialsOnly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"web-actions-{Guid.NewGuid():N}.json");
        try
        {
            AuthFixture fixture = new AuthFixture();
            WebAuthActionCoordinator actions = new(new WebAuthActionStore(path));
            AuthService auth = fixture.Create(actions);
            await auth.InitializeAsync();
            QueuedAttribution? captured = null;
            await auth.CaptureQueuedAsync("report", true, auth.Generation,
                value => { captured = value; return Task.CompletedTask; }, default);
            await auth.SignOutMastodonAsync();
            Assert.DoesNotContain("token", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
            AuthService reopened = fixture.Create(new WebAuthActionCoordinator(new WebAuthActionStore(path)));
            await reopened.InitializeAsync();
            await reopened.SetMastodonAsync("https://example.test", "stale-browser-token");
            Assert.False(reopened.CurrentIdentity?.HasMastodon);
            Assert.True(reopened.CurrentIdentity?.HasBluesky);
            AccountSession retained = await new QueuedCredentialVault(fixture.Storage)
                .ResolveAsync("report", captured!.CredentialReference!);
            Assert.Equal("mastodon-token-a", retained.Mastodon?.Token);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CaptureStartedBeforeSignOutCannotRestoreAccountAfterBrowserAcknowledgement()
    {
        AuthFixture fixture = new AuthFixture();
        WebAuthActionCoordinator actions = new();
        AuthService auth = fixture.Create(actions);
        await auth.InitializeAsync();
        long oldGeneration = auth.Generation;
        await auth.SignOutMastodonAsync();
        actions.Acknowledge(Assert.Single(actions.GetPendingActions()).Id);
        await auth.SetMastodonAsync("https://example.test", "stale", expectedGeneration: oldGeneration);
        Assert.False(auth.CurrentIdentity?.HasMastodon);
    }

    internal static HttpResponseMessage Json<T>(T body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    [Fact]
    public async Task FailedNativeSignOutCannotBeAcknowledgedAndOldCaptureStaysBlockedAfterAcknowledgement()
    {
        AuthFixture fixture = new();
        WebAuthActionCoordinator actions = new();
        AuthService auth = fixture.Create(actions);
        await auth.InitializeAsync();
        fixture.Storage.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => auth.SignOutMastodonAsync());
        fixture.Storage.Fail = false;
        WebAuthAction action = Assert.Single(actions.GetPendingActions());
        await Assert.ThrowsAsync<IOException>(() => auth.AcknowledgeWebSignOutAsync(action));
        Assert.Single(actions.GetPendingActions());
        AuthService reopened = fixture.Create(actions);
        await reopened.InitializeAsync();
        Assert.True(await reopened.AcknowledgeWebSignOutAsync(action));
        await reopened.SetMastodonAsync("https://example.test", "stale");
        Assert.False(reopened.CurrentIdentity?.HasMastodon);
        Assert.True(await reopened.BeginSignInAsync(WebAuthProvider.Mastodon));
        fixture.Native.Response = (_, _) => Task.FromResult(Json(new { id = "b", server = "https://example.test", username = "b" }));
        await reopened.SetMastodonAsync("https://example.test", "new-b");
        Assert.Equal("b", reopened.CurrentIdentity?.MastodonAccountId);
    }
}

internal sealed class TestSecureStorage : ISecureValueStore
{
    public Dictionary<string, string> Values { get; } = [];
    public bool Fail { get; set; }
    public Task<string?> GetAsync(string key) => Fail ? throw new IOException("Secure storage unavailable") :
        Task.FromResult(Values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value)
    {
        if (Fail) throw new IOException("Secure storage unavailable");
        Values[key] = value;
        return Task.CompletedTask;
    }
    public void Remove(string key) => Values.Remove(key);
}

internal sealed class TestHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Response(request, cancellationToken);
}

internal sealed class TestDispatcher : IClientDispatcher
{
    public void Dispatch(Action action) => action();
}

internal sealed class TestCookieBridge : IWebViewCookieBridge
{
    public int Copies;
    public Task CopyWebViewCookiesToAppAsync(CookieContainer container, Uri siteUri)
    { Copies++; return Task.CompletedTask; }
    public Task ClearAsync(Uri siteUri) => Task.CompletedTask;
}

internal sealed class AuthFixture
{
    public TestSecureStorage Storage { get; } = new();
    public TestHttpHandler Native { get; } = new();
    public TestHttpHandler Cookies { get; } = new();
    public TestCookieBridge Bridge { get; } = new();
    public AuthFixture()
    {
        Storage.Values["cbl.active-session.v2"] = JsonSerializer.Serialize(new AccountSession(
            new AccountCredential("did:plc:a", "a.bsky.social", "token-a"),
            new AccountCredential("mastodon-a", "a", "mastodon-token-a", "https://example.test")));
    }
    public AuthService Create(WebAuthActionCoordinator? actions = null) => new AuthService(
        new HttpClient(Cookies), new HttpClient(Native), new CookieContainer(), Bridge, Storage,
        new QueuedCredentialVault(Storage), actions ?? new WebAuthActionCoordinator(),
        new TestDispatcher(), NullLogger<AuthService>.Instance);
}
