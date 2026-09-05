using SeattleCarsInBikeLanes.Mobile.Core.Navigation;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class WebAuthActionCoordinatorTests
{
    [Fact]
    public void KeepsActionPendingUntilAcknowledged()
    {
        WebAuthActionCoordinator coordinator = new WebAuthActionCoordinator();

        WebAuthAction action = coordinator.QueueOpenSignIn(WebAuthProvider.Bluesky);

        Assert.Equal([action], coordinator.GetPendingActions());
        Assert.True(coordinator.Acknowledge(action.Id));
        Assert.Empty(coordinator.GetPendingActions());
    }

    [Fact]
    public void ReplacesPendingSignInWithLatestProvider()
    {
        WebAuthActionCoordinator coordinator = new WebAuthActionCoordinator();

        coordinator.QueueOpenSignIn(WebAuthProvider.Bluesky);
        WebAuthAction mastodon = coordinator.QueueOpenSignIn(WebAuthProvider.Mastodon);

        Assert.Equal([mastodon], coordinator.GetPendingActions());
    }

    [Fact]
    public void CoalescesDuplicateProviderSignOut()
    {
        WebAuthActionCoordinator coordinator = new WebAuthActionCoordinator();

        WebAuthAction first = coordinator.QueueApplySignedOut(WebAuthProvider.Bluesky);
        WebAuthAction duplicate = coordinator.QueueApplySignedOut(WebAuthProvider.Bluesky);

        Assert.Equal(first, duplicate);
        Assert.Equal([first], coordinator.GetPendingActions());
    }

    [Fact]
    public void KeepsEachProviderSignOutPending()
    {
        WebAuthActionCoordinator coordinator = new WebAuthActionCoordinator();

        WebAuthAction bluesky = coordinator.QueueApplySignedOut(WebAuthProvider.Bluesky);
        WebAuthAction mastodon = coordinator.QueueApplySignedOut(WebAuthProvider.Mastodon);

        Assert.Equal([bluesky, mastodon], coordinator.GetPendingActions());
    }

    [Fact]
    public void RaisesChangeNotificationForEveryQueueRequest()
    {
        WebAuthActionCoordinator coordinator = new WebAuthActionCoordinator();
        int notifications = 0;
        coordinator.PendingActionsChanged += (_, _) => notifications++;

        coordinator.QueueApplySignedOut(WebAuthProvider.Bluesky);
        coordinator.QueueApplySignedOut(WebAuthProvider.Bluesky);
        coordinator.QueueOpenSignIn(WebAuthProvider.Mastodon);

        Assert.Equal(3, notifications);
    }

    [Fact]
    public void ReportsWhetherProviderActionIsPending()
    {
        WebAuthActionCoordinator coordinator = new WebAuthActionCoordinator();

        WebAuthAction action = coordinator.QueueApplySignedOut(WebAuthProvider.Mastodon);

        Assert.True(coordinator.HasPending(
            WebAuthActionKind.ApplySignedOut,
            WebAuthProvider.Mastodon));

        coordinator.Acknowledge(action.Id);

        Assert.False(coordinator.HasPending(
            WebAuthActionKind.ApplySignedOut,
            WebAuthProvider.Mastodon));
    }
}
