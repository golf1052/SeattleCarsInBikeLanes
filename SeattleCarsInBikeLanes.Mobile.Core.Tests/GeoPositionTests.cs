using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class GeoPositionTests
{
    private static readonly GeoPosition SpaceNeedle = new GeoPosition(47.6205, -122.3493);

    [Fact]
    public void FormatsCoordinatesTheWayTheApiExpects()
    {
        GeoPosition position = new GeoPosition(47.6062123, -122.3321987);

        Assert.Equal("47.60621", position.LatitudeString);
        Assert.Equal("-122.3322", position.LongitudeString);
    }

    [Fact]
    public void UsesAnInvariantDecimalSeparator()
    {
        System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // A culture where the decimal separator is a comma would otherwise produce coordinates
            // the server cannot parse.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.Equal("47.6062", new GeoPosition(47.6062, -122.3321).LatitudeString);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void MeasuresNothingBetweenAPositionAndItself()
    {
        Assert.Equal(0d, SpaceNeedle.DistanceInMetersTo(SpaceNeedle));
    }

    [Fact]
    public void MeasuresAThousandthOfADegreeOfLatitude()
    {
        GeoPosition north = SpaceNeedle with { Latitude = SpaceNeedle.Latitude + 0.001 };

        Assert.Equal(111.19d, SpaceNeedle.DistanceInMetersTo(north), 1d);
    }

    /// <summary>
    /// A degree of longitude is shorter the further from the equator it is, so the two axes cannot
    /// be treated the same.
    /// </summary>
    [Fact]
    public void MeasuresAThousandthOfADegreeOfLongitude()
    {
        GeoPosition east = SpaceNeedle with { Longitude = SpaceNeedle.Longitude + 0.001 };

        Assert.Equal(74.95d, SpaceNeedle.DistanceInMetersTo(east), 1d);
    }

    [Fact]
    public void MeasuresAcrossDowntown()
    {
        GeoPosition pikePlace = new GeoPosition(47.6097, -122.3422);

        Assert.Equal(1313.5d, SpaceNeedle.DistanceInMetersTo(pikePlace), 5d);
    }

    [Fact]
    public void MeasuresTheSameDistanceEitherWay()
    {
        GeoPosition pikePlace = new GeoPosition(47.6097, -122.3422);

        Assert.Equal(SpaceNeedle.DistanceInMetersTo(pikePlace),
            pikePlace.DistanceInMetersTo(SpaceNeedle),
            0.001d);
    }
}
