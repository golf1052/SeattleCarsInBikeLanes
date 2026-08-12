using SeattleCarsInBikeLanes.Mobile.Core.Camera;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class ZoomRangeTests
{
    [Theory]
    [InlineData(float.NaN, 8f)]
    [InlineData(1f, float.NaN)]
    [InlineData(1f, float.PositiveInfinity)]
    [InlineData(0f, 8f)]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 8f)]
    [InlineData(8f, 1f)]
    public void TreatsNonsenseFromTheCameraAsNoZoom(float minimum, float maximum)
    {
        ZoomRange range = ZoomRange.FromCamera(minimum, maximum);

        Assert.False(range.CanZoom);
        Assert.Equal(ZoomRange.None, range);
    }

    [Fact]
    public void TreatsACameraThatReportsNoRangeAsNoZoom()
    {
        // What CameraX falls back to when it has no zoom state to read.
        ZoomRange range = ZoomRange.FromCamera(1f, 1f);

        Assert.False(range.CanZoom);
    }

    [Fact]
    public void CapsWhatTheDeviceClaimsToSupport()
    {
        ZoomRange range = ZoomRange.FromCamera(1f, 123.75f);

        Assert.Equal(1f, range.Minimum);
        Assert.Equal(ZoomRange.MaximumUsableZoom, range.Maximum);
        Assert.True(range.CanZoom);
    }

    [Fact]
    public void KeepsAMaximumBelowTheCap()
    {
        ZoomRange range = ZoomRange.FromCamera(1f, 6f);

        Assert.Equal(6f, range.Maximum);
    }

    [Fact]
    public void CollapsesACameraWhoseMinimumIsAboveTheCap()
    {
        // A lens whose whole range sits past where the app is willing to go has nothing left to
        // offer, so it should not present a zoom control at all.
        ZoomRange range = ZoomRange.FromCamera(12f, 40f);

        Assert.False(range.CanZoom);
        Assert.Equal(12f, range.Minimum);
        Assert.Equal(12f, range.Maximum);
    }

    [Fact]
    public void ClampsIntoTheRange()
    {
        ZoomRange range = ZoomRange.FromCamera(0.5f, 8f);

        Assert.Equal(0.5f, range.Clamp(0.1f));
        Assert.Equal(8f, range.Clamp(50f));
        Assert.Equal(3f, range.Clamp(3f));
        Assert.Equal(range.Default, range.Clamp(float.NaN));
    }

    [Fact]
    public void OpensAtOneTimes()
    {
        Assert.Equal(1f, ZoomRange.FromCamera(0.5f, 8f).Default);
    }

    [Fact]
    public void OpensAsWideAsATelephotoGoes()
    {
        Assert.Equal(2f, ZoomRange.FromCamera(2f, 8f).Default);
    }

    [Fact]
    public void OffersTheUsualStopsOnAnUltraWideLens()
    {
        ZoomRange range = ZoomRange.FromCamera(0.5f, 8f);

        Assert.Equal([0.5f, 1f, 2f, 8f], range.Presets);
    }

    [Fact]
    public void OnlyOffersTheStopsACameraCanReach()
    {
        ZoomRange range = ZoomRange.FromCamera(1f, 1.8f);

        Assert.Equal([1f, 1.8f], range.Presets);
    }

    [Fact]
    public void DoesNotOfferTheSameStopTwice()
    {
        ZoomRange range = ZoomRange.FromCamera(1f, 2f);

        Assert.Equal([1f, 2f], range.Presets);
    }

    [Fact]
    public void DoesNotOfferAStopAHairFromAnotherOne()
    {
        ZoomRange range = ZoomRange.FromCamera(1f, 2.01f);

        Assert.Equal([1f, 2f], range.Presets);
    }

    [Fact]
    public void DoesNotOfferStopsBelowATelephotoMinimum()
    {
        ZoomRange range = ZoomRange.FromCamera(2f, 8f);

        Assert.Equal([2f, 8f], range.Presets);
    }

    [Fact]
    public void KeepsAWayBackToAnAwkwardMinimum()
    {
        // 1x and 2x both miss a lens that starts at 1.5x, and without its own stop there would be
        // no way to tap back out to the widest view it has.
        ZoomRange range = ZoomRange.FromCamera(1.5f, 8f);

        Assert.Equal([1.5f, 2f, 8f], range.Presets);
        Assert.Equal(1.5f, range.NextPreset(8f));
    }

    [Fact]
    public void OffersASingleStopWhenTheCameraCannotZoom()
    {
        Assert.Equal([1f], ZoomRange.FromCamera(1f, 1f).Presets);
    }

    [Fact]
    public void StepsInThroughTheStops()
    {
        ZoomRange range = ZoomRange.FromCamera(0.5f, 8f);

        Assert.Equal(1f, range.NextPreset(0.5f));
        Assert.Equal(2f, range.NextPreset(1f));
        Assert.Equal(8f, range.NextPreset(2f));
    }

    [Fact]
    public void WrapsBackToTheWidestStop()
    {
        ZoomRange range = ZoomRange.FromCamera(0.5f, 8f);

        Assert.Equal(0.5f, range.NextPreset(8f));
    }

    [Fact]
    public void StepsToTheNextStopAboveAPinchedZoom()
    {
        ZoomRange range = ZoomRange.FromCamera(0.5f, 8f);

        Assert.Equal(2f, range.NextPreset(1.4f));
    }

    [Fact]
    public void StepsFromAZoomOutsideTheRange()
    {
        ZoomRange range = ZoomRange.FromCamera(1f, 8f);

        Assert.Equal(2f, range.NextPreset(0.1f));
        Assert.Equal(1f, range.NextPreset(99f));
    }

    [Fact]
    public void StaysPutWhenThereIsNowhereToGo()
    {
        ZoomRange range = ZoomRange.FromCamera(1f, 1f);

        Assert.Equal(1f, range.NextPreset(1f));
    }

    [Theory]
    [InlineData(1f, "1x")]
    [InlineData(0.5f, "0.5x")]
    [InlineData(1.5f, "1.5x")]
    [InlineData(2f, "2x")]
    [InlineData(10f, "10x")]
    [InlineData(1.04f, "1x")]
    [InlineData(2.349f, "2.3x")]
    [InlineData(float.NaN, "1x")]
    public void LabelsAZoomFactorTheWayACameraAppDoes(float value, string expected)
    {
        Assert.Equal(expected, ZoomRange.Format(value));
    }
}
