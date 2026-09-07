using SeattleCarsInBikeLanes.Mobile.Core.Navigation;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class WebAuthJavaScriptTests
{
    [Theory]
    [InlineData(WebAuthProvider.Bluesky, "bluesky")]
    [InlineData(WebAuthProvider.Mastodon, "mastodon")]
    public void OpenSignInUsesBridge(WebAuthProvider provider, string providerName)
    {
        string script = WebAuthJavaScript.Build(
            new WebAuthAction(1, WebAuthActionKind.OpenSignIn, provider));

        Assert.Contains("carsInBikeLanesMobileAuth", script);
        Assert.Contains($"openSignIn('{providerName}')", script);
        Assert.DoesNotContain("document", script);
    }

    [Theory]
    [InlineData(WebAuthProvider.Bluesky, "bluesky")]
    [InlineData(WebAuthProvider.Mastodon, "mastodon")]
    public void ApplySignedOutUsesBridge(WebAuthProvider provider, string providerName)
    {
        string script = WebAuthJavaScript.Build(
            new WebAuthAction(1, WebAuthActionKind.ApplySignedOut, provider));

        Assert.Contains("carsInBikeLanesMobileAuth", script);
        Assert.Contains($"applySignedOut('{providerName}')", script);
        Assert.DoesNotContain("document", script);
        Assert.DoesNotContain("localStorage", script);
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
