using Microsoft.Maui.Devices;
using SeattleCarsInBikeLanes.Mobile.Core.Camera;

namespace SeattleCarsInBikeLanes.Mobile.Services;

public interface ICameraOrientationSource
{
    event EventHandler? OrientationChanged;

    CameraControlOrientation? Current { get; }
}

public abstract class DeviceDisplayCameraOrientationSource : ICameraOrientationSource
{
    private readonly IDeviceDisplay deviceDisplay;
    private EventHandler? orientationChanged;
    private CameraControlOrientation? lastValidOrientation;
    private CameraControlOrientation? lastPublishedOrientation;

    protected DeviceDisplayCameraOrientationSource()
        : this(DeviceDisplay.Current)
    {
    }

    protected DeviceDisplayCameraOrientationSource(IDeviceDisplay deviceDisplay)
    {
        this.deviceDisplay = deviceDisplay;
    }

    public event EventHandler? OrientationChanged
    {
        add
        {
            bool startListening = orientationChanged is null;
            orientationChanged += value;

            if (startListening)
            {
                lastPublishedOrientation = Current;
                deviceDisplay.MainDisplayInfoChanged += MainDisplayInfoChanged;
            }
        }
        remove
        {
            orientationChanged -= value;

            if (orientationChanged is null)
            {
                deviceDisplay.MainDisplayInfoChanged -= MainDisplayInfoChanged;
            }
        }
    }

    public CameraControlOrientation? Current
    {
        get
        {
            CameraControlOrientation? current = ReadOrientation(deviceDisplay.MainDisplayInfo);
            if (current is not null)
            {
                lastValidOrientation = current;
            }

            return lastValidOrientation;
        }
    }

    protected abstract CameraControlOrientation? ReadOrientation(DisplayInfo displayInfo);

    private void MainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
    {
        if (MainThread.IsMainThread)
        {
            PublishOrientationChange();
            return;
        }

        MainThread.BeginInvokeOnMainThread(PublishOrientationChange);
    }

    private void PublishOrientationChange()
    {
        CameraControlOrientation? current = Current;
        if (current is null || current == lastPublishedOrientation)
        {
            return;
        }

        lastPublishedOrientation = current;
        orientationChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class CameraOrientationSource : DeviceDisplayCameraOrientationSource
{
    protected override CameraControlOrientation? ReadOrientation(DisplayInfo displayInfo) =>
        displayInfo.Orientation == DisplayOrientation.Portrait
            ? CameraControlOrientation.Portrait
            : null;
}
