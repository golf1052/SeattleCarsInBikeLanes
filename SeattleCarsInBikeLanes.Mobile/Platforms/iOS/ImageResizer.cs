using CoreGraphics;
using Foundation;
using ImageIO;
using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Mobile.Services;
using UniformTypeIdentifiers;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

/// <summary>
/// Shrinks photos with ImageIO, keeping their metadata.
/// </summary>
/// <remarks>
/// ImageIO is used rather than UIImage because drawing a UIImage into a new context produces a
/// bitmap with no metadata at all. The server reads the capture date and GPS out of the file it is
/// given, so losing those would push every report into manual entry.
/// </remarks>
public sealed class ImageResizer : IImageResizer
{
    private readonly ILogger<ImageResizer> logger;

    public ImageResizer(ILogger<ImageResizer> logger)
    {
        this.logger = logger;
    }

    public Task<byte[]> ResizeAsync(byte[] jpeg, int maxEdge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        try
        {
            return Task.FromResult(Resize(jpeg, maxEdge) ?? jpeg);
        }
        catch (Exception ex)
        {
            // Sending the original is slower but never wrong, so a resize failure must not stop an
            // upload.
            logger.LogWarning(ex, "Could not resize a photo, uploading it at full size.");
            return Task.FromResult(jpeg);
        }
    }

    private byte[]? Resize(byte[] jpeg, int maxEdge)
    {
        using NSData data = NSData.FromArray(jpeg);
        using CGImageSource? source = CGImageSource.FromData(data);
        if (source is null || source.ImageCount == 0)
        {
            return null;
        }

        CoreGraphics.CGImageProperties? properties = source.GetProperties(0, null);
        nint? width = properties?.PixelWidth;
        nint? height = properties?.PixelHeight;

        if (width is null || height is null)
        {
            return null;
        }

        if (Math.Max(width.Value, height.Value) <= maxEdge)
        {
            // Already small enough. Re-encoding would only lose quality.
            return null;
        }

        CGImageThumbnailOptions options = new CGImageThumbnailOptions()
        {
            CreateThumbnailFromImageAlways = true,
            MaxPixelSize = maxEdge,

            // The orientation tag is carried over untouched, so the pixels must not be rotated as
            // well or the image would end up rotated twice.
            CreateThumbnailWithTransform = false
        };

        using CGImage? resized = source.CreateThumbnail(0, options);
        if (resized is null)
        {
            return null;
        }

        using NSMutableData output = new NSMutableData();
        using CGImageDestination? destination =
            CGImageDestination.Create(output, UTTypes.Jpeg.Identifier, imageCount: 1);

        if (destination is null)
        {
            return null;
        }

        // Carrying the source properties across is what keeps EXIF, GPS and orientation intact.
        NSDictionary? sourceProperties = source.CopyProperties(new CGImageOptions(), 0);
        destination.AddImage(resized, sourceProperties);

        return destination.Close() ? output.ToArray() : null;
    }
}
