using CommunityToolkit.Maui.Views;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Detects when the native camera preview is rendering frames.
/// </summary>
public interface ICameraPreviewReadiness
{
    Task WaitForFirstFrameAsync(CameraView cameraView, CancellationToken cancellationToken);
}

public sealed class UnsupportedCameraPreviewReadiness : ICameraPreviewReadiness
{
    public Task WaitForFirstFrameAsync(CameraView cameraView, CancellationToken cancellationToken) =>
        Task.FromException(new PlatformNotSupportedException("Camera preview readiness is not supported."));
}
