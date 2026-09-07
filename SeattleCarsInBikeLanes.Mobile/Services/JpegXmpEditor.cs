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

        byte[]? existingPacket = JpegSegmentScanner.FindXmpPacket(new MemoryStream(jpeg, writable: false));
        IXmpMeta meta = (existingPacket is null ? null : CarsInBikeLanesXmp.TryParse(existingPacket))
            ?? XmpMetaFactory.Create();

        CarsInBikeLanesXmp.Write(meta, state);

        // The writer truncates and rewrites the stream it is given, so it needs one that can grow.
        // A MemoryStream constructed over an existing array cannot.
        using MemoryStream stream = new MemoryStream();
        stream.Write(jpeg, 0, jpeg.Length);
        stream.Position = 0;

        ImageMetadataWriter.WriteMetadata(stream, new object[] { meta });

        return stream.ToArray();
    }
}
