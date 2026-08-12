using ExifLibrary;
using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Core.Metadata;

/// <summary>
/// Reads and writes the EXIF the site cares about: when the photo was taken and where.
/// </summary>
/// <remarks>
/// The server runs <c>exiftool -createdate</c> and <c>exiftool -gpsposition</c> over every upload.
/// <c>CreateDate</c> is EXIF <c>DateTimeDigitized</c>, so that tag in particular has to be present
/// or the server treats the report as having no date and the user is made to type one in.
/// </remarks>
public static class PhotoExif
{
    /// <summary>
    /// Stamps capture time and location onto a JPEG.
    /// </summary>
    /// <param name="source">The original JPEG. Read from its current position.</param>
    /// <param name="destination">Receives the JPEG with metadata applied.</param>
    /// <remarks>
    /// This writes to a separate stream rather than editing in place because a shrinking EXIF block
    /// would otherwise leave the tail of the previous image behind.
    /// </remarks>
    public static void WriteCaptureMetadata(Stream source,
        Stream destination,
        DateTimeOffset takenAt,
        GeoPosition? location)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        ImageFile image = ImageFile.FromStream(source);
        ApplyCaptureMetadata(image, takenAt, location);
        image.Save(destination);
    }

    /// <summary>
    /// Applies capture time and location to an already parsed image.
    /// </summary>
    public static void ApplyCaptureMetadata(ImageFile image, DateTimeOffset takenAt, GeoPosition? location)
    {
        ArgumentNullException.ThrowIfNull(image);

        DateTime localTakenAt = takenAt.LocalDateTime;

        // EXIF timestamps have no time zone, and are conventionally local time.
        image.Properties.Set(new ExifDateTime(ExifTag.DateTime, localTakenAt));
        image.Properties.Set(new ExifDateTime(ExifTag.DateTimeOriginal, localTakenAt));
        image.Properties.Set(new ExifDateTime(ExifTag.DateTimeDigitized, localTakenAt));

        if (location is null)
        {
            return;
        }

        GeoPosition position = location.Value;

        image.Properties.Set(ToDegreesMinutesSeconds(ExifTag.GPSLatitude, position.Latitude));
        image.Properties.Set(ExifTag.GPSLatitudeRef,
            position.Latitude < 0 ? GPSLatitudeRef.South : GPSLatitudeRef.North);
        image.Properties.Set(ToDegreesMinutesSeconds(ExifTag.GPSLongitude, position.Longitude));
        image.Properties.Set(ExifTag.GPSLongitudeRef,
            position.Longitude < 0 ? GPSLongitudeRef.West : GPSLongitudeRef.East);

        // GPS timestamps are always UTC, unlike the tags above.
        DateTime utcTakenAt = takenAt.UtcDateTime;
        image.Properties.Set(new ExifDate(ExifTag.GPSDateStamp, utcTakenAt.Date));
        image.Properties.Set(new GPSTimeStamp(ExifTag.GPSTimeStamp,
            utcTakenAt.Hour,
            utcTakenAt.Minute,
            utcTakenAt.Second));
    }

    /// <summary>
    /// Reads the capture time and location back out of a JPEG.
    /// </summary>
    /// <remarks>
    /// Photos that came from somewhere else routinely have some or all of this missing, so every
    /// field is optional and a malformed file reads as empty rather than throwing.
    /// </remarks>
    public static PhotoExifData Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            return Read(ImageFile.FromStream(source));
        }
        catch (Exception)
        {
            return PhotoExifData.Empty;
        }
    }

    /// <summary>
    /// Reads the capture time and location from an already parsed image.
    /// </summary>
    public static PhotoExifData Read(ImageFile image)
    {
        ArgumentNullException.ThrowIfNull(image);

        DateTime? takenAt = ReadDate(image, ExifTag.DateTimeOriginal)
            ?? ReadDate(image, ExifTag.DateTimeDigitized)
            ?? ReadDate(image, ExifTag.DateTime);

        return new PhotoExifData(takenAt, ReadLocation(image));
    }

    private static DateTime? ReadDate(ImageFile image, ExifTag tag)
    {
        ExifDateTime? property = image.Properties.Get<ExifDateTime>(tag);
        if (property is null)
        {
            return null;
        }

        // Cameras that know the date but not the time write a zeroed placeholder.
        return property.Value == DateTime.MinValue ? null : property.Value;
    }

    private static GeoPosition? ReadLocation(ImageFile image)
    {
        GPSLatitudeLongitude? latitude = image.Properties.Get<GPSLatitudeLongitude>(ExifTag.GPSLatitude);
        GPSLatitudeLongitude? longitude = image.Properties.Get<GPSLatitudeLongitude>(ExifTag.GPSLongitude);
        if (latitude is null || longitude is null)
        {
            return null;
        }

        ExifEnumProperty<GPSLatitudeRef>? latitudeRef =
            image.Properties.Get<ExifEnumProperty<GPSLatitudeRef>>(ExifTag.GPSLatitudeRef);
        ExifEnumProperty<GPSLongitudeRef>? longitudeRef =
            image.Properties.Get<ExifEnumProperty<GPSLongitudeRef>>(ExifTag.GPSLongitudeRef);

        double latitudeDegrees = ToDecimalDegrees(latitude);
        if (latitudeRef is not null && latitudeRef.Value == GPSLatitudeRef.South)
        {
            latitudeDegrees = -latitudeDegrees;
        }

        double longitudeDegrees = ToDecimalDegrees(longitude);
        if (longitudeRef is not null && longitudeRef.Value == GPSLongitudeRef.West)
        {
            longitudeDegrees = -longitudeDegrees;
        }

        return new GeoPosition(latitudeDegrees, longitudeDegrees);
    }

    /// <summary>
    /// Converts a signed decimal coordinate to the degrees/minutes/seconds triple EXIF stores.
    /// </summary>
    /// <remarks>
    /// The sign is dropped, because EXIF keeps the hemisphere in a separate reference tag.
    /// </remarks>
    public static GPSLatitudeLongitude ToDegreesMinutesSeconds(ExifTag tag, double coordinate)
    {
        double totalSeconds = Math.Abs(coordinate) * 3600.0;
        double degrees = Math.Floor(totalSeconds / 3600.0);
        double minutes = Math.Floor((totalSeconds - (degrees * 3600.0)) / 60.0);
        double seconds = totalSeconds - (degrees * 3600.0) - (minutes * 60.0);

        return new GPSLatitudeLongitude(tag, (float)degrees, (float)minutes, (float)seconds);
    }

    /// <summary>
    /// Converts an EXIF degrees/minutes/seconds triple to unsigned decimal degrees.
    /// </summary>
    public static double ToDecimalDegrees(GPSLatitudeLongitude value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ToDecimalDegrees((double)value.Degrees, (double)value.Minutes, (double)value.Seconds);
    }

    /// <summary>
    /// Converts degrees, minutes and seconds to unsigned decimal degrees.
    /// </summary>
    public static double ToDecimalDegrees(double degrees, double minutes, double seconds)
    {
        return degrees + (minutes / 60.0) + (seconds / 3600.0);
    }
}

/// <summary>
/// The subset of EXIF the app and the site care about.
/// </summary>
public readonly record struct PhotoExifData(DateTime? TakenAt, GeoPosition? Location)
{
    public static PhotoExifData Empty { get; } = new PhotoExifData(null, null);
}
