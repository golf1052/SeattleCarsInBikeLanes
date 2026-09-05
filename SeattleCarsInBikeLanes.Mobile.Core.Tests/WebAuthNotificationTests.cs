using SeattleCarsInBikeLanes.Mobile.Core.Navigation;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class WebAuthNotificationTests
{
    [Theory]
    [InlineData("cibl-mobile://auth/signed-out?provider=bluesky", WebAuthProvider.Bluesky)]
    [InlineData("cibl-mobile://auth/signed-out?provider=mastodon", WebAuthProvider.Mastodon)]
    [InlineData("cibl-mobile://auth/signed-out?unrelated=value&provider=%62luesky", WebAuthProvider.Bluesky)]
    public void ParsesProviderSignOut(string value, WebAuthProvider expected)
    {
        Assert.True(WebAuthNotification.TryGetSignedOutProvider(new Uri(value), out WebAuthProvider provider));
        Assert.Equal(expected, provider);
    }

    [Theory]
    [InlineData("https://auth/signed-out?provider=bluesky")]
    [InlineData("cibl-mobile://other/signed-out?provider=bluesky")]
    [InlineData("cibl-mobile://auth/other?provider=bluesky")]
    [InlineData("cibl-mobile://auth/signed-out?provider=unknown")]
    [InlineData("cibl-mobile://auth/signed-out?provider=Bluesky")]
    [InlineData("cibl-mobile://auth/signed-out")]
    [InlineData("cibl-mobile://auth/signed-out?provider=")]
    [InlineData("cibl-mobile://auth/signed-out?provider=bluesky&provider=mastodon")]
    [InlineData("cibl-mobile://auth/signed-out?provider=%ZZ")]
    public void RejectsInvalidNotification(string value)
    {
        Assert.False(WebAuthNotification.TryGetSignedOutProvider(new Uri(value), out _));
    }

    [Theory]
    [InlineData("cibl-mobile://auth/signed-out?provider=bluesky", true)]
    [InlineData("cibl-mobile://future/action", true)]
    [InlineData("https://seattle.carinbikelane.com/", false)]
    public void IdentifiesReservedNotificationScheme(string value, bool expected)
    {
        Assert.Equal(expected, WebAuthNotification.IsNotificationScheme(new Uri(value)));
    }
}
