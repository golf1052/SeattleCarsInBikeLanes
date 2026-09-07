using System.Globalization;
using System.Text.Json.Serialization;

namespace SeattleCarsInBikeLanes.Mobile.Core.Models;

/// <summary>
/// A WGS84 coordinate.
/// </summary>
public readonly record struct GeoPosition(double Latitude, double Longitude)
{
    /// <summary>
    /// Formats a coordinate the way the upload API expects it.
    /// </summary>
    /// <remarks>
    /// The server parses these with <c>double.Parse</c> and the site sends five decimal places, so
    /// the format is fixed and culture invariant. A comma decimal separator would be parsed as a
    /// different number or rejected outright.
    ///
    /// Ignored when serializing because a position is persisted with the queued reports, and the
    /// rounded strings are derived from the coordinates rather than another copy of them.
    /// </remarks>
    [JsonIgnore]
    public string LatitudeString => Latitude.ToString("0.#####", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string LongitudeString => Longitude.ToString("0.#####", CultureInfo.InvariantCulture);

    public override string ToString() => $"{LatitudeString}, {LongitudeString}";

    /// <summary>
    /// How far apart two positions are, over the ground, in metres.
    /// </summary>
    /// <remarks>
    /// Haversine on a spherical earth. The ellipsoid is worth about 0.3% at worst, which is
    /// nothing next to the tens of metres a phone's fix can be out by in a city, and the distances
    /// this is asked about are a city block rather than a continent.
    /// </remarks>
    public double DistanceInMetersTo(GeoPosition other)
    {
        const double EarthRadiusMeters = 6371000d;

        double latitudeDelta = DegreesToRadians(other.Latitude - Latitude);
        double longitudeDelta = DegreesToRadians(other.Longitude - Longitude);

        double latitude = DegreesToRadians(Latitude);
        double otherLatitude = DegreesToRadians(other.Latitude);

        double a = (Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)) +
            (Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2) *
                Math.Cos(latitude) * Math.Cos(otherLatitude));

        // Atan2 rather than Asin, which loses its precision for the near zero distances this is
        // mostly asked about.
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
