namespace SeattleCarsInBikeLanes.Mobile.Core.Models;

/// <summary>
/// The area the site accepts reports in.
/// </summary>
/// <remarks>
/// The server rejects anything outside this box, so the app checks first and says so before
/// spending a user's cellular data on an upload that cannot succeed. These numbers mirror
/// <c>UploadController.SeattleBoundingBox</c> and are refreshed from <c>/api/Upload/Limits</c>
/// when that call succeeds.
/// </remarks>
public sealed class BoundingBox
{
    public static BoundingBox Seattle { get; } = new BoundingBox(
        southLatitude: 47.495082,
        westLongitude: -122.436522,
        northLatitude: 47.735525,
        eastLongitude: -122.235787);

    public BoundingBox(double southLatitude, double westLongitude, double northLatitude, double eastLongitude)
    {
        SouthLatitude = southLatitude;
        WestLongitude = westLongitude;
        NorthLatitude = northLatitude;
        EastLongitude = eastLongitude;
    }

    public double SouthLatitude { get; }

    public double WestLongitude { get; }

    public double NorthLatitude { get; }

    public double EastLongitude { get; }

    public GeoPosition Center => new GeoPosition(
        (SouthLatitude + NorthLatitude) / 2.0,
        (WestLongitude + EastLongitude) / 2.0);

    public bool Contains(GeoPosition position) =>
        position.Latitude >= SouthLatitude &&
        position.Latitude <= NorthLatitude &&
        position.Longitude >= WestLongitude &&
        position.Longitude <= EastLongitude;
}
