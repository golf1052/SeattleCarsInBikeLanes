namespace SeattleCarsInBikeLanes.Mobile.Core.Camera;

public enum CameraShutterPhase
{
    Began,
    Ended,
    Cancelled
}

/// <summary>
/// Rejects releases from disabled controls or an earlier native attachment.
/// </summary>
public sealed class CameraHardwareInput
{
    private bool pressStarted;

    public int Generation { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Reset()
    {
        Generation++;
        SetEnabled(false);
    }

    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        if (!enabled)
        {
            pressStarted = false;
        }
    }

    public bool IsCurrent(int generation) => generation == Generation;

    public bool Handle(int generation, CameraShutterPhase phase)
    {
        if (!IsCurrent(generation) || !IsEnabled)
        {
            return false;
        }

        if (phase == CameraShutterPhase.Began)
        {
            pressStarted = true;
            return false;
        }

        bool capture = phase == CameraShutterPhase.Ended && pressStarted;
        pressStarted = false;
        return capture;
    }
}
