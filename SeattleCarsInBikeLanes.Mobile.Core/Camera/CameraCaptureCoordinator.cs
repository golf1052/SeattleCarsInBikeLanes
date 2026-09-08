namespace SeattleCarsInBikeLanes.Mobile.Core.Camera;

/// <summary>
/// Serializes UI-thread shutter requests without queuing unwanted extra photos.
/// </summary>
public sealed class CameraCaptureCoordinator
{
    public event EventHandler? StateChanged;

    public bool IsCapturing { get; private set; }

    public async Task CaptureAsync(bool isAvailable, Func<Task> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!isAvailable || IsCapturing)
        {
            return;
        }

        IsCapturing = true;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            await capture();
        }
        finally
        {
            IsCapturing = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
