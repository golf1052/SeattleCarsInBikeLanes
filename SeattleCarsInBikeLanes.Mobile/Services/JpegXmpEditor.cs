using JpegXmpWritePluginMDE.MetadataExtractor;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using XmpCore;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Writes the app's upload flag into a JPEG's XMP packet.
/// </summary>
/// <remarks>
/// Reading XMP is handled in Mobile.Core, but nothing in MetadataExtractor can write it, so this
/// leans on the JpegXmpWritePluginMDE project for the one operation Core cannot do.
/// </remarks>
public static class JpegXmpEditor
{
    /// <summary>
    /// Returns a copy of the JPEG with the upload state applied.
    /// </summary>
    /// <remarks>
    /// Any XMP already in the file is amended rather than replaced. Photos and the camera write
    /// their own properties into the same packet, and throwing those away to record one boolean
    /// would quietly destroy metadata the user never asked us to touch.
    /// </remarks>
    public static byte[] SetUploadState(byte[] jpeg, XmpUploadState state)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        IXmpMeta meta = ReadMetadata(jpeg);
        CarsInBikeLanesXmp.Write(meta, state);
        return WriteMetadata(jpeg, meta);
    }

    /// <summary>
    /// Restores custom XMP after rendering, updating tags that describe the old pixel layout.
    /// </summary>
    public static byte[] CopyUprightXmp(byte[] original, byte[] rendered, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        IXmpMeta meta = ReadMetadata(original);
        meta.SetPropertyInteger("http://ns.adobe.com/tiff/1.0/", "Orientation", 1);
        meta.SetPropertyInteger("http://ns.adobe.com/tiff/1.0/", "ImageWidth", width);
        meta.SetPropertyInteger("http://ns.adobe.com/tiff/1.0/", "ImageLength", height);
        meta.SetPropertyInteger("http://ns.adobe.com/exif/1.0/", "PixelXDimension", width);
        meta.SetPropertyInteger("http://ns.adobe.com/exif/1.0/", "PixelYDimension", height);
        return WriteMetadata(rendered, meta);
    }

    private static IXmpMeta ReadMetadata(byte[] jpeg)
    {
        byte[]? packet = JpegSegmentScanner.FindXmpPacket(new MemoryStream(jpeg, writable: false));
        return (packet is null ? null : CarsInBikeLanesXmp.TryParse(packet)) ?? XmpMetaFactory.Create();
    }

    private static byte[] WriteMetadata(byte[] jpeg, IXmpMeta meta)
    {
        // The writer truncates and rewrites the stream it is given, so it needs one that can grow.
        // A MemoryStream constructed over an existing array cannot.
        using MemoryStream stream = new MemoryStream();
        stream.Write(jpeg, 0, jpeg.Length);
        stream.Position = 0;

        ImageMetadataWriter.WriteMetadata(stream, new object[] { meta });

        return stream.ToArray();
    }
}
