using CoreGraphics;
using CoreImage;
using Foundation;
using ImageIO;
using SeattleCarsInBikeLanes.Mobile.Services;
using UniformTypeIdentifiers;
using ImageProperties = ImageIO.CGImageProperties;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

internal static class PhotoEditingRenderer
{
    public static byte[] RenderUpright(byte[] original, CGImagePropertyOrientation orientation)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (orientation is < CGImagePropertyOrientation.Up or > CGImagePropertyOrientation.Left)
            throw new IOException("The photo has an unsupported orientation.");
        if (orientation == CGImagePropertyOrientation.Up &&
            original.Length >= 2 && original[0] == 0xff && original[1] == 0xd8)
            return original;

        using NSData data = NSData.FromArray(original);
        using CGImageSource source = CGImageSource.FromData(data)
            ?? throw new IOException("The photo could not be opened for rendering.");
        using CGImage pixels = source.CreateImage(0, new CGImageOptions())
            ?? throw new IOException("The photo pixels could not be decoded.");
        using CIImage image = CIImage.FromCGImage(pixels);
        using CIImage upright = image.CreateByApplyingOrientation(orientation);
        using CIContext context = CIContext.FromOptions(null);
        using CGImage rendered = context.CreateCGImage(upright, upright.Extent)
            ?? throw new IOException("The photo could not be rendered upright.");

        using NSDictionary properties = source.CopyProperties(new CGImageOptions(), 0)
            ?? throw new IOException("The photo metadata could not be read.");
        using NSMutableDictionary outputProperties = new NSMutableDictionary(properties);
        outputProperties[ImageProperties.Orientation] = NSNumber.FromInt32(1);
        outputProperties[ImageProperties.PixelWidth] = NSNumber.FromNInt(rendered.Width);
        outputProperties[ImageProperties.PixelHeight] = NSNumber.FromNInt(rendered.Height);
        SetProperties(ImageProperties.TIFFDictionary,
            (ImageProperties.TIFFOrientation, 1));
        SetProperties(ImageProperties.ExifDictionary,
            (ImageProperties.ExifPixelXDimension, rendered.Width),
            (ImageProperties.ExifPixelYDimension, rendered.Height));

        using NSMutableData result = new NSMutableData();
        using CGImageDestination destination = CGImageDestination.Create(result, UTTypes.Jpeg.Identifier, 1)
            ?? throw new IOException("The upright JPEG could not be created.");
        destination.AddImage(rendered,
            new CGImageDestinationOptions(outputProperties) { LossyCompressionQuality = 1 });
        if (!destination.Close())
            throw new IOException("The upright JPEG could not be saved.");
        // ImageIO doesn't reliably round-trip custom XMP. Restore it with the JPEG
        // writer, normalizing its orientation/dimensions to match the baked pixels.
        return JpegXmpEditor.CopyUprightXmp(original, result.ToArray(),
            (int)rendered.Width, (int)rendered.Height);

        void SetProperties(NSString dictionaryKey, params (NSString Key, nint Value)[] values)
        {
            using NSMutableDictionary dictionary = properties[dictionaryKey] is NSDictionary existing
                ? new NSMutableDictionary(existing) : new NSMutableDictionary();
            foreach ((NSString key, nint value) in values)
                dictionary[key] = NSNumber.FromNInt(value);
            outputProperties[dictionaryKey] = dictionary;
        }
    }
}
