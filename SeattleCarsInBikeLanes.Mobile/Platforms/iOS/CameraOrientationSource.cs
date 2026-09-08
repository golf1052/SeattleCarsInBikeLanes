using Microsoft.Maui.Devices;
using SeattleCarsInBikeLanes.Mobile.Core.Camera;
using SeattleCarsInBikeLanes.Mobile.Services;
using UIKit;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

public sealed class CameraOrientationSource : DeviceDisplayCameraOrientationSource
{
    protected override CameraControlOrientation? ReadOrientation(DisplayInfo displayInfo)
    {
        UIWindowScene? scene = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .FirstOrDefault(scene =>
                scene.ActivationState is UISceneActivationState.ForegroundActive
                    or UISceneActivationState.ForegroundInactive);

        UIInterfaceOrientation? orientation = scene is null
            ? null
            : GetInterfaceOrientation(scene);

        // UIInterfaceOrientation names the edge holding the phone's natural bottom.
        return orientation switch
        {
            UIInterfaceOrientation.Portrait or UIInterfaceOrientation.PortraitUpsideDown =>
                CameraControlOrientation.Portrait,
            UIInterfaceOrientation.LandscapeLeft =>
                CameraControlOrientation.LandscapePhysicalBottomLeft,
            UIInterfaceOrientation.LandscapeRight =>
                CameraControlOrientation.LandscapePhysicalBottomRight,
            _ => null
        };
    }

    private static UIInterfaceOrientation GetInterfaceOrientation(UIWindowScene scene) =>
        OperatingSystem.IsIOSVersionAtLeast(26)
            ? scene.EffectiveGeometry.InterfaceOrientation
            : scene.InterfaceOrientation;
}
