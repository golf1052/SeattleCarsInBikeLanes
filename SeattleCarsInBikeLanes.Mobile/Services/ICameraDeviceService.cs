using CommunityToolkit.Maui.Core;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// The parts of the camera hardware the camera toolkit does not expose.
/// </summary>
/// <remarks>
/// The toolkit hands out a flat list of cameras and a zoom factor, and keeps the underlying capture
/// device to itself. Picking a sensible lens out of that list and focusing it both need to go
/// straight to the platform.
/// </remarks>
public interface ICameraDeviceService
{
    /// <summary>
    /// The everyday camera facing a given way.
    /// </summary>
    /// <remarks>
    /// A modern iPhone reports every lens it has, plus the virtual devices that combine them, so a
    /// list of eight cameras is normal. Only one of them is what a person means by "the back
    /// camera".
    /// </remarks>
    CameraInfo? GetMainCamera(IReadOnlyList<CameraInfo> cameras, CameraPosition position);

    /// <summary>
    /// Puts a camera into continuous autofocus, metering on the middle of the frame.
    /// </summary>
    void ResumeContinuousFocus(string deviceId);

    /// <summary>
    /// Focuses and meters on a point of the preview.
    /// </summary>
    /// <param name="deviceId">The camera being previewed.</param>
    /// <param name="preview">The camera view the point was measured against.</param>
    /// <param name="pointInPreview">Where in that view the user tapped.</param>
    /// <returns>True when the camera accepted the request.</returns>
    bool FocusAt(string deviceId, View preview, Point pointInPreview);
}

/// <summary>
/// Picks cameras by the order the platform lists them, and leaves focus alone.
/// </summary>
/// <remarks>
/// Used where there is no platform implementation. On Android this is close to right on its own:
/// CameraX lists the main sensors first, it drives continuous autofocus by default, and it runs its
/// own focus sequence before every capture. Driving that any further would need the CameraX camera
/// control, which the toolkit keeps internal.
/// </remarks>
public sealed class CameraDeviceService : ICameraDeviceService
{
    public CameraInfo? GetMainCamera(IReadOnlyList<CameraInfo> cameras, CameraPosition position)
    {
        ArgumentNullException.ThrowIfNull(cameras);

        return cameras.FirstOrDefault(camera => camera.Position == position);
    }

    public void ResumeContinuousFocus(string deviceId)
    {
    }

    public bool FocusAt(string deviceId, View preview, Point pointInPreview) => false;
}
