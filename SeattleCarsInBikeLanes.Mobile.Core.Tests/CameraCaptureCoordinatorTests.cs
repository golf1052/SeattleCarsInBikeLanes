using SeattleCarsInBikeLanes.Mobile.Core.Camera;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class CameraCaptureCoordinatorTests
{
    [Fact]
    public async Task DoesNotCaptureWhenThePreviewIsUnavailable()
    {
        CameraCaptureCoordinator coordinator = new CameraCaptureCoordinator();
        int captures = 0;

        await coordinator.CaptureAsync(false, () =>
        {
            captures++;
            return Task.CompletedTask;
        });

        Assert.Equal(0, captures);
        Assert.False(coordinator.IsCapturing);
    }

    [Fact]
    public async Task OverlappingShuttersDoNotQueueExtraPhotos()
    {
        CameraCaptureCoordinator coordinator = new CameraCaptureCoordinator();
        TaskCompletionSource completion = new TaskCompletionSource();
        List<bool> availability = [];
        coordinator.StateChanged += (_, _) => availability.Add(coordinator.IsCapturing);
        int captures = 0;

        Task Capture()
        {
            captures++;
            return completion.Task;
        }

        Task first = coordinator.CaptureAsync(true, Capture);
        await coordinator.CaptureAsync(true, Capture);
        Assert.Equal(1, captures);
        Assert.True(coordinator.IsCapturing);
        completion.SetResult();
        await first;

        Assert.False(coordinator.IsCapturing);
        Assert.Equal([true, false], availability);
        await coordinator.CaptureAsync(true, Capture);
        Assert.Equal(2, captures);
    }

    [Fact]
    public async Task FailedCaptureReleasesTheGuardAndPropagatesTheError()
    {
        CameraCaptureCoordinator coordinator = new CameraCaptureCoordinator();
        InvalidOperationException error = new InvalidOperationException("Capture failed");

        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CaptureAsync(true, () => Task.FromException(error))));
        Assert.False(coordinator.IsCapturing);

        bool captured = false;
        await coordinator.CaptureAsync(true, () =>
        {
            captured = true;
            return Task.CompletedTask;
        });
        Assert.True(captured);
    }

    [Fact]
    public async Task SynchronouslyThrownCaptureAlsoReleasesTheGuard()
    {
        CameraCaptureCoordinator coordinator = new CameraCaptureCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CaptureAsync(true, () => throw new InvalidOperationException()));

        Assert.False(coordinator.IsCapturing);
    }

    [Fact]
    public async Task ReturningToAnUnavailablePreviewDoesNotCaptureAgain()
    {
        CameraCaptureCoordinator coordinator = new CameraCaptureCoordinator();
        TaskCompletionSource completion = new TaskCompletionSource();
        Task first = coordinator.CaptureAsync(true, () => completion.Task);
        completion.SetResult();
        await first;

        await coordinator.CaptureAsync(false, () => throw new InvalidOperationException("Hidden capture"));
        Assert.False(coordinator.IsCapturing);
    }
}
