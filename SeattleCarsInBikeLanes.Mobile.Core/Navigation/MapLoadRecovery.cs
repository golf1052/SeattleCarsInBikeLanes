namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

/// <summary>
/// Tracks whether the embedded map needs another navigation after a lost connection.
/// A retry is consumed until another failure or an offline navigation makes it necessary again.
/// </summary>
public sealed class MapLoadRecovery
{
    private bool navigationPending = true;

    public MapLoadRecovery(bool initiallyOffline) => NeedsRetry = initiallyOffline;

    public bool NeedsRetry { get; private set; }

    public void NavigationStarted(bool hasInternet)
    {
        navigationPending = true;
        if (!hasInternet)
        {
            NeedsRetry = true;
        }
    }

    public void NavigationSucceeded(bool hasInternet)
    {
        navigationPending = false;
        NeedsRetry = !hasInternet;
    }

    public void NavigationFailed()
    {
        navigationPending = false;
        NeedsRetry = true;
    }

    public void ConnectivityLost()
    {
        if (navigationPending)
        {
            NeedsRetry = true;
        }
    }

    public bool TryBeginRetry(bool hasInternet, bool isVisible, bool isAuthorizing)
    {
        if (!NeedsRetry || !hasInternet || !isVisible || isAuthorizing)
        {
            return false;
        }

        NeedsRetry = false;
        navigationPending = true;
        return true;
    }
}
