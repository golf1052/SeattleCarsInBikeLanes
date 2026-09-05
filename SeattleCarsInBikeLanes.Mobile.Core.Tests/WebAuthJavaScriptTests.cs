using SeattleCarsInBikeLanes.Mobile.Core.Navigation;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class WebAuthJavaScriptTests
{
    [Fact]
    public void NotificationSetupSupportsEventsAndExistingButtonFallback()
    {
        Assert.Contains("enableNativeNotifications", WebAuthJavaScript.EnableNativeNotifications);
        Assert.Contains("carsInBikeLanesAuthChanged", WebAuthJavaScript.EnableNativeNotifications);
        Assert.Contains("blueskyLogoutButton", WebAuthJavaScript.EnableNativeNotifications);
        Assert.Contains("mastodonLogoutButton", WebAuthJavaScript.EnableNativeNotifications);
        Assert.Contains("cibl-mobile://auth/signed-out", WebAuthJavaScript.EnableNativeNotifications);
    }

    [Theory]
    [InlineData(WebAuthProvider.Bluesky, "bluesky", "blueskyHandleModal")]
    [InlineData(WebAuthProvider.Mastodon, "mastodon", "mastodonServerModal")]
    public void OpenSignInUsesBridgeAndModalFallback(
        WebAuthProvider provider,
        string providerName,
        string modalId)
    {
        string script = WebAuthJavaScript.Build(
            new WebAuthAction(1, WebAuthActionKind.OpenSignIn, provider));

        Assert.Contains("carsInBikeLanesMobileAuth", script);
        Assert.Contains($"openSignIn('{providerName}')", script);
        Assert.Contains(modalId, script);
    }

    [Theory]
    [InlineData(WebAuthProvider.Bluesky, "setBlueskyLoggedOut")]
    [InlineData(WebAuthProvider.Mastodon, "clearMastodonAuth")]
    public void ApplySignedOutUsesBridgeAndExistingFunctionFallback(
        WebAuthProvider provider,
        string fallbackFunction)
    {
        string script = WebAuthJavaScript.Build(
            new WebAuthAction(1, WebAuthActionKind.ApplySignedOut, provider));

        Assert.Contains("carsInBikeLanesMobileAuth", script);
        Assert.Contains("applySignedOut", script);
        Assert.Contains(fallbackFunction, script);
    }

    [Fact]
    public void MastodonFallbackDoesNotNotifyNativeAgain()
    {
        string script = WebAuthJavaScript.Build(
            new WebAuthAction(1, WebAuthActionKind.ApplySignedOut, WebAuthProvider.Mastodon));

        Assert.Contains("clearMastodonAuth(false)", script);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"true\"")]
    public void RecognizesSuccessfulPlatformResults(string result)
    {
        Assert.True(WebAuthJavaScript.WasSuccessful(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("null")]
    public void RejectsUnsuccessfulPlatformResults(string? result)
    {
        Assert.False(WebAuthJavaScript.WasSuccessful(result));
    }
}
