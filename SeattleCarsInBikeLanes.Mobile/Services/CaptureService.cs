using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Prepares a freshly captured photo for storage.
/// </summary>
public interface ICaptureService
{
    /// <summary>
    /// Stamps a captured photo with when and where it was taken, and marks it as not yet submitted.
    /// </summary>
    Task<byte[]> PrepareCapturedPhotoAsync(Stream media, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class CaptureService : ICaptureService
{
    /// <summary>
    /// How long to wait for a fix before saving the photo without one.
    /// </summary>
    /// <remarks>
    /// A photo with no location is a mild inconvenience the user can fix by dropping a pin. Making
    /// them wait, or losing the shot outright, is not a trade worth making while they are standing
    /// next to a bike lane.
    /// </remarks>
    private static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(4);

    private readonly IGeolocation geolocation;
    private readonly ILogger<CaptureService> logger;

    public CaptureService(IGeolocation geolocation, ILogger<CaptureService> logger)
    {
        this.geolocation = geolocation;
        this.logger = logger;
    }

    public async Task<byte[]> PrepareCapturedPhotoAsync(Stream media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);

        using MemoryStream original = new MemoryStream();
        await media.CopyToAsync(original, cancellationToken);
        original.Position = 0;

        DateTimeOffset takenAt = DateTimeOffset.Now;
        GeoPosition? location = await TryGetLocationAsync(cancellationToken);

        using MemoryStream withExif = new MemoryStream();
        try
        {
            PhotoExif.WriteCaptureMetadata(original, withExif, takenAt, location);
        }
        catch (Exception ex)
        {
            // Better an unstamped photo than a lost one. The server will ask the user for the
            // details it could not read.
            logger.LogError(ex, "Could not write capture metadata, saving the photo unchanged.");
            original.Position = 0;
            return original.ToArray();
        }

        try
        {
            withExif.Position = 0;
            return JpegXmpEditor.SetUploadState(withExif.ToArray(), XmpUploadState.NotUploaded);
        }
        catch (Exception ex)
        {
            // Without the flag the photo simply reads as not submitted, which is what it is.
            logger.LogError(ex, "Could not stamp the upload state onto a captured photo.");
            return withExif.ToArray();
        }
    }

    private async Task<GeoPosition?> TryGetLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            PermissionStatus permission =
                await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (permission != PermissionStatus.Granted)
            {
                return null;
            }

            GeolocationRequest request = new GeolocationRequest(GeolocationAccuracy.Best, LocationTimeout);
            Location? location = await geolocation.GetLocationAsync(request, cancellationToken);

            return location is null ? null : new GeoPosition(location.Latitude, location.Longitude);
        }
        catch (Exception ex)
        {
            // Permission refused, location services off, indoors with no fix: all of these are
            // ordinary and none of them should cost the user their photo.
            logger.LogInformation(ex, "No location was available for a captured photo.");
            return null;
        }
    }
}
