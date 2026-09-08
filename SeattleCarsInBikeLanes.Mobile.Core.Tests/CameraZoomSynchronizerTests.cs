using SeattleCarsInBikeLanes.Mobile.Core.Camera;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class CameraZoomSynchronizerTests
{
    private static CameraZoomSynchronizer CreateSynchronizer() =>
        new CameraZoomSynchronizer(ZoomRange.FromSystemRecommendation(1f, 15f, 1f, 20f)!.Value);

    [Fact]
    public void HardwareZoomUsesQuarterTimesSteps()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();

        Assert.Equal(0.25f, sync.SliderStep);
        Assert.Equal(4f, 1f / sync.SliderStep);
    }

    [Fact]
    public void HardwareStepDoesNotExceedANarrowCameraRange()
    {
        CameraZoomSynchronizer sync = new CameraZoomSynchronizer(ZoomRange.FromCamera(1f, 1.1f));

        Assert.True(sync.Range.CanZoom);
        Assert.Equal(sync.Range.Maximum - sync.Range.Minimum, sync.SliderStep);
    }

    [Fact]
    public void CoarserHardwareStepsDoNotRoundPinchZoomOrEchoItBack()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(1f, 1f);

        Assert.Equal(2.37f, sync.ReadDeviceChange(2.37f, 1f));
        Assert.Null(sync.ReadSliderChange(2.37f, 2.37f, 2.37f));
        Assert.Equal(2.5f, sync.ReadSliderChange(2.5f, 2.5f, 2.37f));
    }

    [Fact]
    public void InitializesTheSliderFromTheCurrentCameraZoom()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();

        Assert.Equal(4f, sync.ReadDeviceChange(4f, 1f));
        Assert.Null(sync.ReadDeviceChange(4f, 4f));
    }

    [Fact]
    public void PinchAndPillChangesUpdateTheNativeSliderWithoutEchoingBack()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(1f, 1f);

        Assert.Equal(2.5f, sync.ReadDeviceChange(2.5f, 1f));
        Assert.Null(sync.ReadSliderChange(2.5f, 2.5f, 2.5f));
        Assert.Equal(15f, sync.ReadDeviceChange(15f, 2.5f));
        Assert.Null(sync.ReadSliderChange(15f, 15f, 15f));
    }

    [Fact]
    public void HardwareChangesUpdateTheDeviceWithoutResettingTheSlider()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(1f, 1f);

        Assert.Equal(3f, sync.ReadSliderChange(3f, 3f, 1f));
        sync.AcceptSliderChange(3f);
        Assert.Null(sync.ReadDeviceChange(3f, 3f));
        Assert.Null(sync.ReadSliderChange(3f, 3f, 3f));
    }

    [Fact]
    public void QueuedEchoFromAnOlderPinchCannotUndoTheLatestZoom()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(1f, 1f);
        Assert.Equal(2f, sync.ReadDeviceChange(2f, 1f));
        Assert.Equal(4f, sync.ReadDeviceChange(4f, 2f));

        Assert.Null(sync.ReadSliderChange(2f, 4f, 4f));
        Assert.Null(sync.ReadSliderChange(4f, 4f, 4f));
    }

    [Fact]
    public void UnchangedNativeNotificationsDoNotCancelAPendingHardwareAction()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(2f, 2f);

        Assert.Null(sync.ReadDeviceChange(2f, 3f));
        Assert.Equal(3f, sync.ReadSliderChange(3f, 3f, 2f));
    }

    [Fact]
    public void CompletingAnEarlierSwipeDoesNotOverwriteALaterSwipe()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(1f, 1f);
        Assert.Equal(2f, sync.ReadSliderChange(2f, 2f, 1f));

        sync.AcceptSliderChange(2f);
        Assert.Null(sync.ReadDeviceChange(2f, 3f));
        Assert.Null(sync.ReadSliderChange(2f, 3f, 2f));
        Assert.Equal(3f, sync.ReadSliderChange(3f, 3f, 2f));
    }

    [Fact]
    public void ANewTouchZoomSupersedesAPendingHardwareAction()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(1f, 1f);

        Assert.Equal(4f, sync.ReadDeviceChange(4f, 3f, force: true));
        Assert.Null(sync.ReadSliderChange(3f, 4f, 4f));
    }

    [Fact]
    public void ResetToTheSameDeviceZoomStillCancelsAPendingHardwareAction()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(1f, 1f);

        Assert.Equal(1f, sync.ReadDeviceChange(1f, 3f, force: true));
        Assert.Null(sync.ReadSliderChange(3f, 1f, 1f));
    }

    [Fact]
    public void FailedDeviceWriteRestoresTheSliderToTheActualZoom()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(1f, 1f);
        Assert.Equal(3f, sync.ReadSliderChange(3f, 3f, 1f));

        Assert.Equal(1f, sync.ReadDeviceChange(1f, 3f, force: true));
        Assert.Null(sync.ReadSliderChange(1f, 1f, 1f));
    }

    [Fact]
    public void RebuiltRangeClampsTheInitialValueAndUsesItsNewBounds()
    {
        CameraZoomSynchronizer sync = new CameraZoomSynchronizer(ZoomRange.FromCamera(1f, 6f));

        Assert.Equal(6f, sync.ReadDeviceChange(10f, 1f));
        Assert.Equal(6f, sync.ReadSliderChange(8f, 8f, 1f));
    }

    [Fact]
    public void IgnoresNumericalNoiseAndInvalidCallbacks()
    {
        CameraZoomSynchronizer sync = CreateSynchronizer();
        sync.ReadDeviceChange(2f, 2f);

        Assert.Null(sync.ReadDeviceChange(2.00001f, 2f));
        Assert.Null(sync.ReadSliderChange(2.00001f, 2.00001f, 2f));
        Assert.Null(sync.ReadSliderChange(float.NaN, float.NaN, 2f));
        Assert.Null(sync.ReadSliderChange(float.PositiveInfinity, float.PositiveInfinity, 2f));
    }
}
