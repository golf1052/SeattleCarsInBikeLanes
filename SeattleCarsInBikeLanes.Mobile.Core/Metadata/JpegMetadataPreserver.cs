namespace SeattleCarsInBikeLanes.Mobile.Core.Metadata;

/// <summary>
/// Replaces a JPEG's application metadata with metadata from an original image.
/// </summary>
public static class JpegMetadataPreserver
{
    public static byte[]? CopyApplicationMetadata(byte[] original, byte[] resized)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(resized);

        if (!TryReadHeader(original, out List<JpegSegment>? originalSegments, out _) ||
            !TryReadHeader(resized, out List<JpegSegment>? resizedSegments, out int scanOffset))
        {
            return null;
        }

        List<JpegSegment> metadata = originalSegments
            .Where(segment => IsApplicationMetadata(segment.Marker) && segment.Marker != 0xe0)
            .ToList();
        if (metadata.Count == 0)
        {
            return resized;
        }

        using MemoryStream output = new MemoryStream(
            resized.Length + metadata.Sum(segment => segment.Length));
        output.Write(resized, 0, 2);

        foreach (JpegSegment segment in resizedSegments.Where(segment => segment.Marker == 0xe0))
        {
            output.Write(resized, segment.Offset, segment.Length);
        }

        foreach (JpegSegment segment in metadata)
        {
            output.Write(original, segment.Offset, segment.Length);
        }

        foreach (JpegSegment segment in resizedSegments.Where(segment =>
            !IsApplicationMetadata(segment.Marker)))
        {
            output.Write(resized, segment.Offset, segment.Length);
        }

        output.Write(resized, scanOffset, resized.Length - scanOffset);
        return output.ToArray();
    }

    private static bool TryReadHeader(
        byte[] jpeg,
        out List<JpegSegment> segments,
        out int scanOffset)
    {
        segments = new List<JpegSegment>();
        scanOffset = 0;

        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
        {
            return false;
        }

        int offset = 2;
        while (offset < jpeg.Length)
        {
            int markerOffset = offset;
            if (jpeg[offset] != 0xff)
            {
                return false;
            }

            while (offset < jpeg.Length && jpeg[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= jpeg.Length)
            {
                return false;
            }

            byte marker = jpeg[offset++];
            if (marker == 0xda || marker == 0xd9)
            {
                scanOffset = markerOffset;
                return true;
            }

            if (marker == 0x00)
            {
                return false;
            }

            if (marker == 0x01 || marker is >= 0xd0 and <= 0xd8)
            {
                segments.Add(new JpegSegment(markerOffset, offset - markerOffset, marker));
                continue;
            }

            if (offset + 2 > jpeg.Length)
            {
                return false;
            }

            int payloadLength = (jpeg[offset] << 8) | jpeg[offset + 1];
            if (payloadLength < 2 || payloadLength > jpeg.Length - offset)
            {
                return false;
            }

            offset += payloadLength;
            segments.Add(new JpegSegment(markerOffset, offset - markerOffset, marker));
        }

        return false;
    }

    private static bool IsApplicationMetadata(byte marker) =>
        marker is >= 0xe0 and <= 0xef || marker == 0xfe;

    private readonly record struct JpegSegment(int Offset, int Length, byte Marker);
}
