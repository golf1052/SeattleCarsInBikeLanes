using SeattleCarsInBikeLanes.Mobile.Core.Metadata;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class JpegMetadataPreserverTests
{
    [Fact]
    public void CopiesExifAndXmpIntoResizedPixels()
    {
        const string xmp = "<x:xmpmeta>upload-state</x:xmpmeta>";
        byte[] original = JpegBuilder.Build(xmp, includeExifApp1: true);
        byte[] resized = JpegBuilder.Build();

        byte[]? result = JpegMetadataPreserver.CopyApplicationMetadata(original, resized);

        Assert.NotNull(result);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(xmp),
            JpegSegmentScanner.FindXmpPacket(new MemoryStream(result)));
        Assert.True(Contains(result, System.Text.Encoding.ASCII.GetBytes("Exif\0\0")));
        Assert.True(Contains(result, new byte[] { 0x12, 0x34, 0x56, 0x78 }));
    }

    [Fact]
    public void ReturnsNullForMalformedInput()
    {
        Assert.Null(JpegMetadataPreserver.CopyApplicationMetadata(
            new byte[] { 1, 2, 3 },
            JpegBuilder.Build()));
        Assert.Null(JpegMetadataPreserver.CopyApplicationMetadata(
            JpegBuilder.Build(),
            new byte[] { 1, 2, 3 }));
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
