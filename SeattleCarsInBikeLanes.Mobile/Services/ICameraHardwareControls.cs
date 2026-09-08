using CommunityToolkit.Maui.Views;
using SeattleCarsInBikeLanes.Mobile.Core.Camera;

namespace SeattleCarsInBikeLanes.Mobile.Services;

public readonly record struct CameraHardwareStatus(
    string? DeviceId,
    bool IsReady,
    ZoomRange? ZoomRange,
    float? ZoomFactor,
    bool IsFullscreen);

/// <summary>
/// Owns hardware interactions, but not the toolkit's camera or capture session.
/// All members and events use the UI thread.
/// </summary>
public interface ICameraHardwareControls
{
    event EventHandler? CaptureRequested;
    event EventHandler? StateChanged;

    CameraHardwareStatus Status { get; }

    void Attach(CameraView camera);
    void Refresh();
    void SetEnabled(bool enabled);
    void SetZoom(float factor);
    void Detach();
}

public sealed class UnsupportedCameraHardwareControls : ICameraHardwareControls
{
    public event EventHandler? CaptureRequested { add { } remove { } }
    public event EventHandler? StateChanged { add { } remove { } }

    public CameraHardwareStatus Status => default;

    public void Attach(CameraView camera) { }
    public void Refresh() { }
    public void SetEnabled(bool enabled) { }
    public void Detach() { }

    public void SetZoom(float factor) => throw new NotSupportedException("Native camera zoom is unavailable.");
}
