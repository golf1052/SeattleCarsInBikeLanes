namespace SeattleCarsInBikeLanes.Mobile.Services;

public interface ICameraAppLifecycle
{
    event EventHandler? Stopped;

    event EventHandler? Resumed;
}

/// <summary>
/// Relays window lifecycle events only when the camera page was active.
/// </summary>
public sealed class CameraAppLifecycle : ICameraAppLifecycle
{
    public event EventHandler? Stopped;

    public event EventHandler? Resumed;

    public void NotifyStopped() => Stopped?.Invoke(this, EventArgs.Empty);

    public void NotifyResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}
