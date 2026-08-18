namespace SeattleCarsInBikeLanes.Mobile.Core.Camera;

public enum CameraControlOrientation
{
    Portrait,
    LandscapePhysicalBottomLeft,
    LandscapePhysicalBottomRight
}

public enum CameraControlLayoutState
{
    Portrait,
    LandscapeRailLeft,
    LandscapeRailRight
}

public static class CameraControlLayoutResolver
{
    public static CameraControlLayoutState Resolve(CameraControlOrientation? orientation,
        CameraControlLayoutState? previousState = null) =>
        orientation switch
        {
            CameraControlOrientation.Portrait => CameraControlLayoutState.Portrait,
            CameraControlOrientation.LandscapePhysicalBottomLeft =>
                CameraControlLayoutState.LandscapeRailLeft,
            CameraControlOrientation.LandscapePhysicalBottomRight =>
                CameraControlLayoutState.LandscapeRailRight,
            null => previousState ?? CameraControlLayoutState.Portrait,
            _ => previousState ?? CameraControlLayoutState.Portrait
        };
}
