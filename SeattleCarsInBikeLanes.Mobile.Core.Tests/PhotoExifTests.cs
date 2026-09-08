using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class PhotoExifTests
{
    /// <summary>
    /// A real, if tiny, JPEG. ExifLibrary rejects hand assembled ones.
    /// </summary>
    private static byte[] BlankJpeg() => File.ReadAllBytes(Path.Combine("TestFiles", "blank.jpg"));

    private static MemoryStream WriteCaptureMetadata(DateTimeOffset takenAt, GeoPosition? location)
    {
        MemoryStream destination = new MemoryStream();
        PhotoExif.WriteCaptureMetadata(new MemoryStream(BlankJpeg()), destination, takenAt, location);
        destination.Position = 0;
        return destination;
    }

    [Fact]
    public void WritesTheTimestampsTheServerReads()
    {
        DateTimeOffset takenAt = new DateTimeOffset(2026, 4, 1, 9, 30, 0, TimeSpan.FromHours(-7));

        PhotoExifData data = PhotoExif.Read(WriteCaptureMetadata(takenAt, null));

        // The server runs exiftool -createdate, which is DateTimeDigitized, so all three timestamps
        // are written and any of them reading back correctly means the tag survived.
        Assert.NotNull(data.TakenAt);
        Assert.Equal(takenAt.LocalDateTime, data.TakenAt!.Value);
    }

    [Fact]
    public void RoundTripsAPositiveCoordinatePair()
    {
        GeoPosition position = new GeoPosition(47.6062, -122.3321);

        PhotoExifData data = PhotoExif.Read(WriteCaptureMetadata(DateTimeOffset.Now, position));

        Assert.NotNull(data.Location);
        Assert.Equal(position.Latitude, data.Location!.Value.Latitude, precision: 4);
        Assert.Equal(position.Longitude, data.Location!.Value.Longitude, precision: 4);
    }

    [Fact]
    public void RoundTripsASouthernAndEasternCoordinate()
    {
        // EXIF stores coordinates unsigned with the hemisphere in a separate tag, so the sign
        // handling is worth checking in both directions.
        GeoPosition position = new GeoPosition(-33.8688, 151.2093);

        PhotoExifData data = PhotoExif.Read(WriteCaptureMetadata(DateTimeOffset.Now, position));

        Assert.NotNull(data.Location);
        Assert.Equal(position.Latitude, data.Location!.Value.Latitude, precision: 4);
        Assert.Equal(position.Longitude, data.Location!.Value.Longitude, precision: 4);
    }

    [Fact]
    public void APhotoWithNoLocationReadsAsHavingNone()
    {
        PhotoExifData data = PhotoExif.Read(WriteCaptureMetadata(DateTimeOffset.Now, null));

        Assert.Null(data.Location);
    }

    [Fact]
    public void APhotoWithNoMetadataAtAllReadsAsEmpty()
    {
        PhotoExifData data = PhotoExif.Read(new MemoryStream(BlankJpeg()));

        Assert.Null(data.TakenAt);
        Assert.Null(data.Location);
    }

    [Fact]
    public void UnreadableDataDoesNotThrow()
    {
        PhotoExifData data = PhotoExif.Read(new MemoryStream("not an image"u8.ToArray()));

        Assert.Equal(PhotoExifData.Empty, data);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(47, 36, 22.32, 47.6062)]
    [InlineData(122, 19, 55.56, 122.3321)]
    public void ConvertsDegreesMinutesSecondsToDecimal(double degrees, double minutes, double seconds, double expected)
    {
        Assert.Equal(expected, PhotoExif.ToDecimalDegrees(degrees, minutes, seconds), precision: 4);
    }

    [Fact]
    public void DropsTheSignWhenConvertingToDegreesMinutesSeconds()
    {
        ExifLibrary.GPSLatitudeLongitude negative =
            PhotoExif.ToDegreesMinutesSeconds(ExifLibrary.ExifTag.GPSLatitude, -47.6062);

        // The hemisphere lives in GPSLatitudeRef, so the triple itself must be unsigned.
        Assert.Equal(47, (double)negative.Degrees, precision: 4);
        Assert.Equal(47.6062, PhotoExif.ToDecimalDegrees(negative), precision: 4);
    }
}
