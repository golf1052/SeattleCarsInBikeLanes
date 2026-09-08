namespace SeattleCarsInBikeLanes.Mobile.Core.Camera;

/// <summary>
/// Keeps a native slider in step with the device without replaying queued value-change echoes.
/// </summary>
public sealed class CameraZoomSynchronizer(ZoomRange range)
{
    private float? lastDeviceZoom;

    public ZoomRange Range { get; } = range;

    // Coarser hardware steps avoid lengthy scrolling; programmatic Value updates remain exact.
    public float SliderStep => Math.Min(0.25f, Range.Maximum - Range.Minimum);

    public void AcceptSliderChange(float deviceZoom) => lastDeviceZoom = Range.Clamp(deviceZoom);

    public float? ReadDeviceChange(float deviceZoom, float sliderValue, bool force = false)
    {
        float value = Range.Clamp(deviceZoom);
        bool changed = lastDeviceZoom is null || !SameValue(lastDeviceZoom.Value, value);
        lastDeviceZoom = value;

        // An unchanged device notification must not overwrite a hardware gesture whose
        // slider value has changed but whose action is still waiting on the dispatch queue.
        return (changed || force) && !SameValue(value, sliderValue) ? value : null;
    }

    public float? ReadSliderChange(float callbackValue, float sliderValue, float deviceZoom)
    {
        if (!float.IsFinite(callbackValue) || !SameValue(callbackValue, sliderValue))
        {
            return null;
        }

        float value = Range.Clamp(callbackValue);
        return SameValue(value, deviceZoom) ? null : value;
    }

    private static bool SameValue(float left, float right) => Math.Abs(left - right) < 0.0001f;
}
