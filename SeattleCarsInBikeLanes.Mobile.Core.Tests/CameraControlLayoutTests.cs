using SeattleCarsInBikeLanes.Mobile.Core.Camera;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class CameraControlLayoutTests
{
    [Fact]
    public void PortraitUsesPortraitState()
    {
        CameraControlLayoutState state = CameraControlLayoutResolver.Resolve(
            CameraControlOrientation.Portrait);

        Assert.Equal(CameraControlLayoutState.Portrait, state);
    }

    [Theory]
    [InlineData(CameraControlOrientation.LandscapePhysicalBottomLeft,
        CameraControlLayoutState.LandscapeRailLeft)]
    [InlineData(CameraControlOrientation.LandscapePhysicalBottomRight,
        CameraControlLayoutState.LandscapeRailRight)]
    public void LandscapeUsesRailOnPhysicalBottom(
        CameraControlOrientation orientation,
        CameraControlLayoutState expectedState)
    {
        CameraControlLayoutState state = CameraControlLayoutResolver.Resolve(orientation);

        Assert.Equal(expectedState, state);
    }

    [Fact]
    public void UnknownOrientationKeepsPreviousState()
    {
        const CameraControlLayoutState previous = CameraControlLayoutState.LandscapeRailRight;

        CameraControlLayoutState state = CameraControlLayoutResolver.Resolve(null, previous);

        Assert.Equal(previous, state);
    }

    [Fact]
    public void UnknownOrientationStartsInPortrait()
    {
        CameraControlLayoutState state = CameraControlLayoutResolver.Resolve(null);

        Assert.Equal(CameraControlLayoutState.Portrait, state);
    }

    [Fact]
    public void InvalidOrientationKeepsPreviousState()
    {
        const CameraControlLayoutState previous = CameraControlLayoutState.LandscapeRailLeft;

        CameraControlLayoutState state = CameraControlLayoutResolver.Resolve(
            (CameraControlOrientation)int.MaxValue,
            previous);

        Assert.Equal(previous, state);
    }
}
