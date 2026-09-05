using SeattleCarsInBikeLanes.Mobile.Core.Navigation;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class WebNavigationPolicyTests
{
    private static readonly Uri SiteBaseAddress = new Uri("https://seattle.carinbikelane.com/");
    private readonly WebNavigationPolicy policy = new WebNavigationPolicy();

    [Fact]
    public void ShouldOpenExternally_KeepsSiteNavigationInWebView()
    {
        Uri target = new Uri(SiteBaseAddress, "blueskyredirect?code=code&state=state");

        Assert.Equal(WebNavigationAction.StayInWebView,
            policy.GetAction(target, SiteBaseAddress));
    }

    [Fact]
    public void ShouldOpenExternally_OpensOrdinaryOffSiteLinksInBrowser()
    {
        Uri target = new Uri("https://bsky.app/profile/seattle.carinbikelane.com");

        Assert.Equal(WebNavigationAction.OpenExternally,
            policy.GetAction(target, SiteBaseAddress));
    }

    [Fact]
    public void ShouldOpenExternally_KeepsBlueskyAuthorizationInWebView()
    {
        Uri target = new Uri(
            "https://bsky.social/oauth/authorize" +
            "?client_id=https%3A%2F%2Fseattle.carinbikelane.com%2Fclient-metadata.json" +
            "&request_uri=urn%3Aietf%3Aparams%3Aoauth%3Arequest_uri%3Arequest-123");

        Assert.Equal(WebNavigationAction.RestartSocialAuthorization,
            policy.GetAction(target, SiteBaseAddress));
    }

    [Fact]
    public void ResetSocialAuthorization_StopsTreatingOffSitePagesAsAuthorization()
    {
        Uri authorization = new Uri(
            "https://mastodon.social/oauth/authorize" +
            "?response_type=code" +
            "&client_id=client-123" +
            "&redirect_uri=https%3A%2F%2Fseattle.carinbikelane.com%2Fmastodonredirect");

        Assert.Equal(WebNavigationAction.RestartSocialAuthorization,
            policy.GetAction(authorization, SiteBaseAddress));

        policy.ResetSocialAuthorization();

        Assert.Equal(WebNavigationAction.OpenExternally,
            policy.GetAction(new Uri("https://mastodon.social/@someone"), SiteBaseAddress));
    }

    [Fact]
    public void ShouldOpenExternally_KeepsEntireBlueskyRedirectChainInWebView()
    {
        Uri authorization = new Uri(
            "https://bsky.social/oauth/authorize" +
            "?client_id=https%3A%2F%2Fseattle.carinbikelane.com%2Fclient-metadata.json" +
            "&request_uri=urn%3Aietf%3Aparams%3Aoauth%3Arequest_uri%3Arequest-123");
        Uri authorizationRedirect = new Uri(
            "https://bsky.social/oauth/authorize/redirect" +
            "?redirect_uri=https%3A%2F%2Fseattle.carinbikelane.com%2Fblueskyredirect" +
            "&state=state&error=access_denied");

        Assert.Equal(WebNavigationAction.RestartSocialAuthorization,
            policy.GetAction(authorization, SiteBaseAddress));
        Assert.Equal(WebNavigationAction.StayInWebView,
            policy.GetAction(authorizationRedirect, SiteBaseAddress));
        Assert.Equal(WebNavigationAction.StayInWebView,
            policy.GetAction(
                new Uri(SiteBaseAddress, "blueskyredirect?state=state&error=access_denied"),
                SiteBaseAddress));
        Assert.Equal(WebNavigationAction.OpenExternally,
            policy.GetAction(
                new Uri("https://bsky.app/profile/seattle.carinbikelane.com"),
                SiteBaseAddress));
    }

    [Fact]
    public void ShouldOpenExternally_KeepsEntireMastodonRedirectChainInWebView()
    {
        Uri authorization = new Uri(
            "https://mastodon.social/oauth/authorize" +
            "?response_type=code" +
            "&client_id=client-123" +
            "&redirect_uri=https%3A%2F%2Fseattle.carinbikelane.com%2Fmastodonredirect" +
            "&scope=read%3Aaccounts");

        Assert.Equal(WebNavigationAction.RestartSocialAuthorization,
            policy.GetAction(authorization, SiteBaseAddress));
        Assert.Equal(WebNavigationAction.StayInWebView,
            policy.GetAction(
                new Uri(SiteBaseAddress, "mastodonredirect?code=code-123"),
                SiteBaseAddress));
        Assert.Equal(WebNavigationAction.OpenExternally,
            policy.GetAction(
                new Uri("https://mastodon.social/@someone"),
                SiteBaseAddress));
    }

    [Theory]
    [InlineData("http://bsky.social/oauth/authorize?client_id=https%3A%2F%2Fseattle.carinbikelane.com%2Fclient-metadata.json&request_uri=urn%3Aietf%3Aparams%3Aoauth%3Arequest_uri%3Arequest-123")]
    [InlineData("https://bsky.social/oauth/authorize?client_id=https%3A%2F%2Fevil.example%2Fclient-metadata.json&request_uri=urn%3Aietf%3Aparams%3Aoauth%3Arequest_uri%3Arequest-123")]
    [InlineData("https://bsky.social/oauth/authorize?client_id=https%3A%2F%2Fseattle.carinbikelane.com%2Fclient-metadata.json")]
    public void ShouldOpenExternally_RejectsInvalidBlueskyAuthorization(string url)
    {
        Assert.Equal(WebNavigationAction.OpenExternally,
            policy.GetAction(new Uri(url), SiteBaseAddress));
    }

    [Theory]
    [InlineData("http://mastodon.social/oauth/authorize?response_type=code&client_id=client-123&redirect_uri=https%3A%2F%2Fseattle.carinbikelane.com%2Fmastodonredirect")]
    [InlineData("https://mastodon.social/oauth/authorize?response_type=token&client_id=client-123&redirect_uri=https%3A%2F%2Fseattle.carinbikelane.com%2Fmastodonredirect")]
    [InlineData("https://mastodon.social/oauth/authorize?response_type=code&redirect_uri=https%3A%2F%2Fseattle.carinbikelane.com%2Fmastodonredirect")]
    [InlineData("https://mastodon.social/oauth/authorize?response_type=code&client_id=client-123&redirect_uri=https%3A%2F%2Fevil.example%2Fmastodonredirect")]
    public void ShouldOpenExternally_RejectsInvalidMastodonAuthorization(string url)
    {
        Assert.Equal(WebNavigationAction.OpenExternally,
            policy.GetAction(new Uri(url), SiteBaseAddress));
    }
}
