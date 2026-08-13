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
}
