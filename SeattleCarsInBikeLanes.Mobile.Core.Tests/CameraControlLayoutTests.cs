using SeattleCarsInBikeLanes.Mobile.Core.Camera;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class CameraControlLayoutTests
{
    [Fact]
    public void PortraitUsesHorizontalBottomRail()
    {
        CameraControlLayout layout = CameraControlLayoutResolver.Resolve(
            CameraControlOrientation.Portrait);

        Assert.Equal(CameraControlEdge.Bottom, layout.Edge);
        Assert.Equal(CameraControlAxis.Horizontal, layout.Axis);
    }

    [Theory]
    [InlineData(CameraControlOrientation.LandscapePhysicalBottomLeft, CameraControlEdge.Left)]
    [InlineData(CameraControlOrientation.LandscapePhysicalBottomRight, CameraControlEdge.Right)]
    public void LandscapeUsesVerticalRailOnPhysicalBottom(
        CameraControlOrientation orientation,
        CameraControlEdge expectedEdge)
    {
        CameraControlLayout layout = CameraControlLayoutResolver.Resolve(orientation);

        Assert.Equal(expectedEdge, layout.Edge);
        Assert.Equal(CameraControlAxis.Vertical, layout.Axis);
    }

    [Fact]
    public void UnknownOrientationKeepsPreviousLayout()
    {
        CameraControlLayout previous =
            new(CameraControlEdge.Right, CameraControlAxis.Vertical);

        CameraControlLayout layout = CameraControlLayoutResolver.Resolve(null, previous);

        Assert.Equal(previous, layout);
    }

    [Fact]
    public void UnknownOrientationStartsInPortrait()
    {
        CameraControlLayout layout = CameraControlLayoutResolver.Resolve(null);

        Assert.Equal(CameraControlEdge.Bottom, layout.Edge);
        Assert.Equal(CameraControlAxis.Horizontal, layout.Axis);
    }
}
