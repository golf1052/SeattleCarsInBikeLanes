using System.Buffers.Binary;
using System.Text;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

/// <summary>
/// Builds small synthetic JPEGs so the metadata code can be tested without binary fixtures.
/// </summary>
/// <remarks>
/// The scanner only cares about segment structure, never about pixels, so a valid marker sequence
/// with a stub scan is enough and keeps the tests readable.
/// </remarks>
internal static class JpegBuilder
{
    private const byte MarkerPrefix = 0xFF;

    /// <summary>
    /// A JPEG with a JFIF APP0, optional XMP APP1, a quantization table, and image data.
    /// </summary>
    public static byte[] Build(string? xmpPacket = null,
        bool includeExifApp1 = false,
        bool includeXmpAfterOtherSegments = false)
    {
        using MemoryStream stream = new MemoryStream();

        // SOI
        stream.WriteByte(MarkerPrefix);
        stream.WriteByte(0xD8);

        WriteSegment(stream, 0xE0,
            Encoding.ASCII.GetBytes("JFIF\0").Concat(new byte[] { 1, 2, 0, 0, 1, 0, 1, 0, 0 }).ToArray());

        if (includeExifApp1)
        {
            WriteSegment(stream, 0xE1,
                Encoding.ASCII.GetBytes("Exif\0\0").Concat(new byte[] { 0x49, 0x49, 0x2A, 0x00 }).ToArray());
        }

        if (xmpPacket is not null && !includeXmpAfterOtherSegments)
        {
            WriteXmpSegment(stream, xmpPacket);
        }

        // DQT, standing in for the segments that normally follow the metadata.
        WriteSegment(stream, 0xDB, new byte[65]);

        if (xmpPacket is not null && includeXmpAfterOtherSegments)
        {
            WriteXmpSegment(stream, xmpPacket);
        }

        // SOS, after which everything is entropy coded image data.
        WriteSegment(stream, 0xDA, new byte[] { 1, 1, 0, 0, 63, 0 });
        stream.Write(new byte[] { 0x12, 0x34, 0x56, 0x78 });

        // EOI
        stream.WriteByte(MarkerPrefix);
        stream.WriteByte(0xD9);

        return stream.ToArray();
    }

    private static void WriteXmpSegment(Stream stream, string packet)
    {
        byte[] payload = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0")
            .Concat(Encoding.UTF8.GetBytes(packet))
            .ToArray();
        WriteSegment(stream, 0xE1, payload);
    }

    private static void WriteSegment(Stream stream, byte marker, byte[] payload)
    {
        stream.WriteByte(MarkerPrefix);
        stream.WriteByte(marker);

        byte[] length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        stream.Write(length);
        stream.Write(payload);
    }
}
