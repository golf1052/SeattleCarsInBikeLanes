using SeattleCarsInBikeLanes.Mobile.Core.Camera;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class CameraHardwareInputTests
{
    [Fact]
    public void CapturesOnceOnReleaseNotOnPress()
    {
        CameraHardwareInput input = new CameraHardwareInput();
        input.SetEnabled(true);

        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Began));
        Assert.True(input.Handle(input.Generation, CameraShutterPhase.Ended));
        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Ended));
    }

    [Fact]
    public void DoesNotCaptureAnUnmatchedRelease()
    {
        CameraHardwareInput input = new CameraHardwareInput();
        input.SetEnabled(true);

        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Ended));
    }

    [Fact]
    public void CancelledPressDoesNotCapture()
    {
        CameraHardwareInput input = new CameraHardwareInput();
        input.SetEnabled(true);
        input.Handle(input.Generation, CameraShutterPhase.Began);

        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Cancelled));
        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Ended));
    }

    [Fact]
    public void DisabledPreviewDoesNotCapture()
    {
        CameraHardwareInput input = new CameraHardwareInput();

        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Began));
        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Ended));
    }

    [Fact]
    public void LeavingAndReturningDuringAPressDiscardsItsRelease()
    {
        CameraHardwareInput input = new CameraHardwareInput();
        input.SetEnabled(true);
        input.Handle(input.Generation, CameraShutterPhase.Began);
        input.SetEnabled(false);
        input.SetEnabled(true);

        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Ended));
        input.Handle(input.Generation, CameraShutterPhase.Began);
        Assert.True(input.Handle(input.Generation, CameraShutterPhase.Ended));
    }

    [Fact]
    public void RefreshingAnEnabledPreviewDoesNotCancelItsPress()
    {
        CameraHardwareInput input = new CameraHardwareInput();
        input.SetEnabled(true);
        input.Handle(input.Generation, CameraShutterPhase.Began);
        input.SetEnabled(true);

        Assert.True(input.Handle(input.Generation, CameraShutterPhase.Ended));
    }

    [Fact]
    public void OldAttachmentCannotCaptureOrCancelANewPress()
    {
        CameraHardwareInput input = new CameraHardwareInput();
        int oldGeneration = input.Generation;
        input.SetEnabled(true);
        input.Handle(oldGeneration, CameraShutterPhase.Began);
        input.Reset();

        Assert.False(input.IsEnabled);
        Assert.False(input.IsCurrent(oldGeneration));
        input.SetEnabled(true);
        input.Handle(input.Generation, CameraShutterPhase.Began);
        Assert.False(input.Handle(oldGeneration, CameraShutterPhase.Cancelled));
        Assert.False(input.Handle(oldGeneration, CameraShutterPhase.Ended));
        Assert.True(input.Handle(input.Generation, CameraShutterPhase.Ended));
    }

    [Fact]
    public void HoldingTheButtonDoesNotCreateABurst()
    {
        CameraHardwareInput input = new CameraHardwareInput();
        input.SetEnabled(true);
        for (int i = 0; i < 10; i++)
        {
            Assert.False(input.Handle(input.Generation, CameraShutterPhase.Began));
        }
        Assert.True(input.Handle(input.Generation, CameraShutterPhase.Ended));
        Assert.False(input.Handle(input.Generation, CameraShutterPhase.Ended));
    }
}
