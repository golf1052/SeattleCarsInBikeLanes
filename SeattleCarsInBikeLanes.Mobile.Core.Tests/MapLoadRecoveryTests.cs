using SeattleCarsInBikeLanes.Mobile.Core.Navigation;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class MapLoadRecoveryTests
{
    [Fact]
    public void OfflineFirstLoadRetriesOnceWhenInternetReturns()
    {
        MapLoadRecovery recovery = new(initiallyOffline: true);

        Assert.False(recovery.TryBeginRetry(hasInternet: false, isVisible: true, isAuthorizing: false));
        Assert.True(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
        Assert.False(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));

        recovery.NavigationSucceeded(hasInternet: true);
        Assert.False(recovery.NeedsRetry);
    }

    [Fact]
    public void FailureWhileOfflineCanRetryAfterReconnection()
    {
        MapLoadRecovery recovery = new(initiallyOffline: false);
        recovery.NavigationStarted(hasInternet: false);
        recovery.NavigationFailed();

        Assert.True(recovery.NeedsRetry);
        Assert.True(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
    }

    [Fact]
    public void LosingInternetDuringAnUnfinishedLoadCanRecoverWithoutAFailureCallback()
    {
        MapLoadRecovery recovery = new(initiallyOffline: false);
        recovery.NavigationStarted(hasInternet: true);
        recovery.ConnectivityLost();

        Assert.True(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
    }

    [Fact]
    public void HiddenPageWaitsUntilItReappears()
    {
        MapLoadRecovery recovery = new(initiallyOffline: false);
        recovery.NavigationFailed();

        Assert.False(recovery.TryBeginRetry(hasInternet: true, isVisible: false, isAuthorizing: false));
        Assert.True(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
    }

    [Fact]
    public void SignInIsNotInterruptedByAnAutomaticRetry()
    {
        MapLoadRecovery recovery = new(initiallyOffline: true);

        Assert.False(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: true));
        Assert.True(recovery.NeedsRetry);
    }

    [Fact]
    public void AFailedRetryCanBeRequestedOnTheNextReconnectOrPageReturn()
    {
        MapLoadRecovery recovery = new(initiallyOffline: true);
        Assert.True(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
        recovery.NavigationFailed();

        Assert.True(recovery.NeedsRetry);
        Assert.True(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
        Assert.False(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
    }

    [Fact]
    public void ALoadedMapIsNotReloadedWhenConnectivityChanges()
    {
        MapLoadRecovery recovery = new(initiallyOffline: true);
        recovery.NavigationSucceeded(hasInternet: true);
        recovery.ConnectivityLost();

        Assert.False(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
    }

    [Fact]
    public void SuccessfulLoadClearsAPendingRetry()
    {
        MapLoadRecovery recovery = new(initiallyOffline: true);
        recovery.NavigationStarted(hasInternet: false);
        recovery.NavigationSucceeded(hasInternet: true);

        Assert.False(recovery.NeedsRetry);
    }

    [Fact]
    public void CachedPageLoadedOfflineIsRefreshedWhenInternetReturns()
    {
        MapLoadRecovery recovery = new(initiallyOffline: true);
        recovery.NavigationSucceeded(hasInternet: false);

        Assert.True(recovery.TryBeginRetry(hasInternet: true, isVisible: true, isAuthorizing: false));
    }
}
