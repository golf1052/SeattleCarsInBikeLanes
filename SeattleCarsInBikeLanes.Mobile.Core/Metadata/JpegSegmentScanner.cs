using System.Buffers.Binary;
using System.Text;

namespace SeattleCarsInBikeLanes.Mobile.Core.Metadata;

/// <summary>
/// How a scan for the XMP packet ended.
/// </summary>
public enum JpegScanOutcome
{
    /// <summary>
    /// The packet was found.
    /// </summary>
    Found,

    /// <summary>
    /// The file is a JPEG, and it definitively has no XMP packet.
    /// </summary>
    NotPresent,

    /// <summary>
    /// The data ran out before the answer was known. More bytes would help.
    /// </summary>
    Incomplete,

    /// <summary>
    /// The data is not a JPEG at all.
    /// </summary>
    NotJpeg
}

/// <summary>
/// Finds the XMP packet in a JPEG without reading the whole file.
/// </summary>
/// <remarks>
/// The photo roll needs the submitted flag for every visible photo, and the flag lives in XMP. On
/// iOS the only way to get a photo's bytes is to stream them out of the Photos library, and a 12MP
/// photo is several megabytes. XMP sits in an APP1 segment near the front of the file, so this
/// reads forward, stops at the image data, and typically needs a few tens of kilobytes.
///
/// <see cref="JpegScanOutcome.Incomplete"/> is why this returns an outcome rather than just a
/// packet: a caller streaming chunks out of the Photos library needs to tell "not there" apart from
/// "keep going", so it knows when it can cancel the rest of the transfer.
/// </remarks>
public static class JpegSegmentScanner
{
    private const byte MarkerPrefix = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;
    private const byte App1 = 0xE1;

    /// <summary>
    /// Signature that identifies an APP1 segment as XMP, including its terminating null.
    /// </summary>
    public const string XmpSignature = "http://ns.adobe.com/xap/1.0/\0";

    private static readonly byte[] XmpSignatureBytes = Encoding.ASCII.GetBytes(XmpSignature);

    /// <summary>
    /// Reads forward through <paramref name="stream"/> looking for the XMP packet.
    /// </summary>
    public static JpegScanOutcome TryFindXmpPacket(Stream stream, out byte[]? packet)
    {
        ArgumentNullException.ThrowIfNull(stream);

        packet = null;

        byte[] soi = new byte[2];
        if (!TryReadExactly(stream, soi, 2))
        {
            return JpegScanOutcome.Incomplete;
        }

        if (soi[0] != MarkerPrefix || soi[1] != StartOfImage)
        {
            return JpegScanOutcome.NotJpeg;
        }

        while (true)
        {
            MarkerResult marker = ReadNextMarker(stream);
            if (marker.RanOut)
            {
                return JpegScanOutcome.Incomplete;
            }

            if (marker.Value is null)
            {
                // The byte stream stopped looking like a marker sequence, so we have wandered off
                // the end of the metadata and there is nothing more to find.
                return JpegScanOutcome.NotPresent;
            }

            byte code = marker.Value.Value;
            if (code == StartOfScan || code == EndOfImage)
            {
                // Image data starts here. Anything past this point is not metadata.
                return JpegScanOutcome.NotPresent;
            }

            if (IsStandaloneMarker(code))
            {
                continue;
            }

            byte[] lengthBytes = new byte[2];
            if (!TryReadExactly(stream, lengthBytes, 2))
            {
                return JpegScanOutcome.Incomplete;
            }

            // The stored length includes the two length bytes themselves.
            int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes) - 2;
            if (length < 0)
            {
                return JpegScanOutcome.NotJpeg;
            }

            if (code != App1)
            {
                if (!TrySkip(stream, length))
                {
                    return JpegScanOutcome.Incomplete;
                }

                continue;
            }

            byte[] payload = new byte[length];
            if (!TryReadExactly(stream, payload, length))
            {
                return JpegScanOutcome.Incomplete;
            }

            if (StartsWithXmpSignature(payload))
            {
                int packetLength = payload.Length - XmpSignatureBytes.Length;
                packet = new byte[packetLength];
                Array.Copy(payload, XmpSignatureBytes.Length, packet, 0, packetLength);
                return JpegScanOutcome.Found;
            }
        }
    }

    /// <summary>
    /// Reads the XMP packet, or null when the JPEG has none.
    /// </summary>
    public static byte[]? FindXmpPacket(Stream stream)
    {
        TryFindXmpPacket(stream, out byte[]? packet);
        return packet;
    }

    /// <summary>
    /// Reads the XMP packet and interprets it as the app's upload state.
    /// </summary>
    public static XmpUploadState ReadUploadState(Stream stream)
    {
        byte[]? packet = FindXmpPacket(stream);
        return packet is null
            ? XmpUploadState.NotUploaded
            : CarsInBikeLanesXmp.Read(CarsInBikeLanesXmp.TryParse(packet));
    }

    private static bool StartsWithXmpSignature(byte[] payload)
    {
        if (payload.Length < XmpSignatureBytes.Length)
        {
            return false;
        }

        for (int i = 0; i < XmpSignatureBytes.Length; i++)
        {
            if (payload[i] != XmpSignatureBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the next marker code, skipping the fill bytes a marker may be padded with.
    /// </summary>
    private static MarkerResult ReadNextMarker(Stream stream)
    {
        int b = stream.ReadByte();
        if (b < 0)
        {
            return MarkerResult.OutOfData;
        }

        if (b != MarkerPrefix)
        {
            return MarkerResult.NotAMarker;
        }

        // Markers are introduced by 0xFF, and any number of extra 0xFF bytes may precede the code.
        while (true)
        {
            int next = stream.ReadByte();
            if (next < 0)
            {
                return MarkerResult.OutOfData;
            }

            if (next != MarkerPrefix)
            {
                return MarkerResult.Marker((byte)next);
            }
        }
    }

    /// <summary>
    /// Markers that carry no payload, so there is no length field to read.
    /// </summary>
    private static bool IsStandaloneMarker(byte marker)
    {
        // TEM, and the restart markers RST0 through RST7.
        return marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7);
    }

    private static bool TrySkip(Stream stream, int count)
    {
        byte[] scratch = new byte[Math.Min(Math.Max(count, 1), 8192)];
        int remaining = count;
        while (remaining > 0)
        {
            int read = stream.Read(scratch, 0, Math.Min(remaining, scratch.Length));
            if (read <= 0)
            {
                return false;
            }

            remaining -= read;
        }

        return true;
    }

    private static bool TryReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        int remaining = count;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, offset, remaining);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
            remaining -= read;
        }

        return true;
    }

    private readonly record struct MarkerResult(byte? Value, bool RanOut)
    {
        public static MarkerResult OutOfData { get; } = new MarkerResult(null, true);

        public static MarkerResult NotAMarker { get; } = new MarkerResult(null, false);

        public static MarkerResult Marker(byte value) => new MarkerResult(value, false);
    }
}
