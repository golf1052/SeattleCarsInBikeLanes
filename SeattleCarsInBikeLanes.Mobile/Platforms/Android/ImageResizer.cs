using Android.Graphics;
using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

/// <summary>
/// Shrinks JPEGs with Android bitmap APIs while retaining the original metadata segments.
/// </summary>
public sealed class ImageResizer : IImageResizer
{
    private const int JpegQuality = 90;

    private readonly ILogger<ImageResizer> logger;

    public ImageResizer(ILogger<ImageResizer> logger)
    {
        this.logger = logger;
    }

    public async Task<byte[]> ResizeAsync(
        byte[] jpeg,
        int maxEdge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        try
        {
            return await Task.Run(
                () => Resize(jpeg, maxEdge, cancellationToken) ?? jpeg,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Sending the original is slower but preserves correctness when Android cannot decode,
            // resize, encode, or merge a particular image.
            logger.LogWarning(exception, "Could not resize a photo, uploading it at full size.");
            return jpeg;
        }
    }

    private static byte[]? Resize(byte[] jpeg, int maxEdge, CancellationToken cancellationToken)
    {
        if (maxEdge <= 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using BitmapFactory.Options bounds = new BitmapFactory.Options
        {
            InJustDecodeBounds = true
        };
        BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length, bounds);

        int width = bounds.OutWidth;
        int height = bounds.OutHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if (Math.Max(width, height) <= maxEdge)
        {
            // Re-encoding an already-small photo would only reduce quality.
            return null;
        }

        double scale = (double)maxEdge / Math.Max(width, height);
        int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        using BitmapFactory.Options decodeOptions = new BitmapFactory.Options
        {
            InSampleSize = CalculateSampleSize(width, height, targetWidth, targetHeight)
        };
        using Bitmap? decoded = BitmapFactory.DecodeByteArray(
            jpeg,
            0,
            jpeg.Length,
            decodeOptions);
        if (decoded is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using Bitmap? resized = Bitmap.CreateScaledBitmap(
            decoded,
            targetWidth,
            targetHeight,
            filter: true);
        if (resized is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using MemoryStream encoded = new MemoryStream();
        if (!resized.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, encoded))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return JpegMetadataPreserver.CopyApplicationMetadata(jpeg, encoded.ToArray());
    }

    private static int CalculateSampleSize(
        int width,
        int height,
        int targetWidth,
        int targetHeight)
    {
        int sampleSize = 1;
        while (sampleSize <= int.MaxValue / 2 &&
            width / (sampleSize * 2) >= targetWidth &&
            height / (sampleSize * 2) >= targetHeight)
        {
            sampleSize *= 2;
        }

        return sampleSize;
    }

}
