using AVFoundation;
using CommunityToolkit.Maui.Core;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Mobile.Services;
using UIKit;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

/// <summary>
/// Asks AVFoundation the things the camera toolkit does not pass on.
/// </summary>
public sealed class CameraDeviceService : ICameraDeviceService
{
    /// <summary>
    /// The middle of the frame, in the coordinates AVFoundation meters in.
    /// </summary>
    private static readonly CGPoint Centre = new CGPoint(0.5, 0.5);

    private readonly ILogger<CameraDeviceService> logger;

    public CameraDeviceService(ILogger<CameraDeviceService> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Finds the plain wide angle camera facing a given way.
    /// </summary>
    /// <remarks>
    /// The toolkit's list holds each physical lens and each virtual multi lens device, and nothing
    /// on <see cref="CameraInfo"/> says which is which, so the answer has to come from AVFoundation.
    /// The wide angle lens is the one to want: its zoom factor of 1 is the field of view everyone
    /// calls 1x, whereas on a virtual device 1 is the ultra wide and every number the app displays
    /// would be half what the user expects.
    /// </remarks>
    public CameraInfo? GetMainCamera(IReadOnlyList<CameraInfo> cameras, CameraPosition position)
    {
        ArgumentNullException.ThrowIfNull(cameras);

        try
        {
            AVCaptureDevicePosition platformPosition = position switch
            {
                CameraPosition.Front => AVCaptureDevicePosition.Front,
                CameraPosition.Rear => AVCaptureDevicePosition.Back,
                _ => AVCaptureDevicePosition.Unspecified
            };

            if (platformPosition is not AVCaptureDevicePosition.Unspecified)
            {
                using AVCaptureDeviceDiscoverySession session = AVCaptureDeviceDiscoverySession.Create(
                    [AVCaptureDeviceType.BuiltInWideAngleCamera],
                    AVMediaTypes.Video,
                    platformPosition);

                foreach (AVCaptureDevice device in session.Devices)
                {
                    CameraInfo? match = cameras.FirstOrDefault(camera =>
                        string.Equals(camera.DeviceId, device.UniqueID, StringComparison.Ordinal));

                    if (match is not null)
                    {
                        return match;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not ask AVFoundation for the main {Position} camera.", position);
        }

        // Whatever the platform listed first for that position. Not necessarily the lens a person
        // would pick, but it faces the right way, which is the part that matters.
        return cameras.FirstOrDefault(camera => camera.Position == position);
    }

    public void ResumeContinuousFocus(string deviceId) => Configure(deviceId, device =>
    {
        ApplyFocus(device, Centre);
    });

    /// <summary>
    /// Focuses and meters where the user tapped the preview.
    /// </summary>
    /// <remarks>
    /// The tap has to be turned into a point on the sensor, which depends on how the device is
    /// held and on the preview cropping the frame to fill its bounds. The preview layer is the only
    /// thing that knows both, so the conversion is left to it rather than worked out here.
    /// </remarks>
    public bool FocusAt(string deviceId, View preview, Point pointInPreview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (preview.Handler?.PlatformView is not UIView platformView ||
            platformView.Layer is not AVCaptureVideoPreviewLayer previewLayer)
        {
            return false;
        }

        CGPoint pointOfInterest = previewLayer.CaptureDevicePointOfInterestForPoint(
            new CGPoint(pointInPreview.X, pointInPreview.Y));

        return Configure(deviceId, device => ApplyFocus(device, pointOfInterest));
    }

    /// <summary>
    /// Aims the camera's focus and exposure at a point.
    /// </summary>
    /// <remarks>
    /// Continuous rather than a single shot on purpose. A one off focus holds wherever it landed
    /// until something else moves it, so a user who taps to focus on a car and then turns to line
    /// the shot up better would be left with a blurred frame and no clue why.
    /// <para>
    /// Nothing is written that is already set. Moving the point of interest starts the lens hunting
    /// again, and this runs on the way to the shutter, so a pointless write would mean photos taken
    /// mid hunt.
    /// </para>
    /// </remarks>
    private static void ApplyFocus(AVCaptureDevice device, CGPoint pointOfInterest)
    {
        if (device.FocusPointOfInterestSupported && !IsSamePoint(device.FocusPointOfInterest, pointOfInterest))
        {
            device.FocusPointOfInterest = pointOfInterest;
        }

        if (device.FocusMode is not AVCaptureFocusMode.ContinuousAutoFocus &&
            device.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
        {
            device.FocusMode = AVCaptureFocusMode.ContinuousAutoFocus;
        }

        if (device.ExposurePointOfInterestSupported && !IsSamePoint(device.ExposurePointOfInterest, pointOfInterest))
        {
            device.ExposurePointOfInterest = pointOfInterest;
        }

        if (device.ExposureMode is not AVCaptureExposureMode.ContinuousAutoExposure &&
            device.IsExposureModeSupported(AVCaptureExposureMode.ContinuousAutoExposure))
        {
            device.ExposureMode = AVCaptureExposureMode.ContinuousAutoExposure;
        }
    }

    /// <summary>
    /// Whether two points of interest are close enough to be the same spot on the sensor.
    /// </summary>
    private static bool IsSamePoint(CGPoint left, CGPoint right) =>
        Math.Abs(left.X - right.X) < 0.001 && Math.Abs(left.Y - right.Y) < 0.001;

    /// <summary>
    /// Runs a change against a camera, holding the lock it requires.
    /// </summary>
    /// <returns>True when the change was applied.</returns>
    private bool Configure(string deviceId, Action<AVCaptureDevice> configure)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return false;
        }

        AVCaptureDevice? device = AVCaptureDevice.DeviceWithUniqueID(deviceId);
        if (device is null)
        {
            return false;
        }

        if (!device.LockForConfiguration(out NSError? error) || error is not null)
        {
            // Something else holds the device, which is temporary and not worth telling the user
            // about. The camera keeps working, just without this change.
            logger.LogInformation("Could not lock the camera to change focus: {Error}.", error?.LocalizedDescription);
            return false;
        }

        try
        {
            configure(device);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change the camera's focus.");
            return false;
        }
        finally
        {
            device.UnlockForConfiguration();
        }
    }
}
