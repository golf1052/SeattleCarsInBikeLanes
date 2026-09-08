using Microsoft.Maui.Devices;
using SeattleCarsInBikeLanes.Mobile.Core.Camera;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

public sealed class CameraOrientationSource : DeviceDisplayCameraOrientationSource
{
    protected override CameraControlOrientation? ReadOrientation(DisplayInfo displayInfo) =>
        displayInfo.Rotation switch
        {
            DisplayRotation.Rotation0 or DisplayRotation.Rotation180 =>
                CameraControlOrientation.Portrait,
            DisplayRotation.Rotation90 =>
                CameraControlOrientation.LandscapePhysicalBottomRight,
            DisplayRotation.Rotation270 =>
                CameraControlOrientation.LandscapePhysicalBottomLeft,
            _ => null
        };
}
