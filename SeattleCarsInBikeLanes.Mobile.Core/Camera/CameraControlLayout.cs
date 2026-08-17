namespace SeattleCarsInBikeLanes.Mobile.Core.Camera;

public enum CameraControlOrientation
{
    Portrait,
    LandscapePhysicalBottomLeft,
    LandscapePhysicalBottomRight
}

public enum CameraControlEdge
{
    Bottom,
    Left,
    Right
}

public enum CameraControlAxis
{
    Horizontal,
    Vertical
}

public readonly record struct CameraControlLayout(CameraControlEdge Edge, CameraControlAxis Axis);

public static class CameraControlLayoutResolver
{
    private static readonly CameraControlLayout PortraitLayout =
        new(CameraControlEdge.Bottom, CameraControlAxis.Horizontal);

    public static CameraControlLayout Resolve(CameraControlOrientation? orientation,
        CameraControlLayout? previousLayout = null) =>
        orientation switch
        {
            CameraControlOrientation.Portrait => PortraitLayout,
            CameraControlOrientation.LandscapePhysicalBottomLeft =>
                new CameraControlLayout(CameraControlEdge.Left, CameraControlAxis.Vertical),
            CameraControlOrientation.LandscapePhysicalBottomRight =>
                new CameraControlLayout(CameraControlEdge.Right, CameraControlAxis.Vertical),
            null => previousLayout ?? PortraitLayout,
            _ => previousLayout ?? PortraitLayout
        };
}
