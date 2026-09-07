using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using XmpCore;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class JpegSegmentScannerTests
{
    [Fact]
    public void FindsTheXmpPacket()
    {
        byte[] jpeg = JpegBuilder.Build(xmpPacket: "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>");

        JpegScanOutcome outcome = JpegSegmentScanner.TryFindXmpPacket(new MemoryStream(jpeg), out byte[]? packet);

        Assert.Equal(JpegScanOutcome.Found, outcome);
        Assert.NotNull(packet);
        Assert.StartsWith("<x:xmpmeta", System.Text.Encoding.UTF8.GetString(packet!), StringComparison.Ordinal);
    }

    [Fact]
    public void SkipsPastAnExifApp1ToReachTheXmpApp1()
    {
        byte[] jpeg = JpegBuilder.Build(xmpPacket: "<x:xmpmeta/>", includeExifApp1: true);

        Assert.Equal(JpegScanOutcome.Found,
            JpegSegmentScanner.TryFindXmpPacket(new MemoryStream(jpeg), out byte[]? packet));
        Assert.NotNull(packet);
    }

    [Fact]
    public void FindsXmpThatSitsAfterOtherSegments()
    {
        byte[] jpeg = JpegBuilder.Build(xmpPacket: "<x:xmpmeta/>", includeXmpAfterOtherSegments: true);

        Assert.Equal(JpegScanOutcome.Found,
            JpegSegmentScanner.TryFindXmpPacket(new MemoryStream(jpeg), out _));
    }

    [Fact]
    public void ReportsNotPresentForAJpegWithoutXmp()
    {
        byte[] jpeg = JpegBuilder.Build();

        Assert.Equal(JpegScanOutcome.NotPresent,
            JpegSegmentScanner.TryFindXmpPacket(new MemoryStream(jpeg), out byte[]? packet));
        Assert.Null(packet);
    }

    [Fact]
    public void StopsAtTheStartOfScanRatherThanReadingTheWholeFile()
    {
        byte[] jpeg = JpegBuilder.Build();
        MemoryStream stream = new MemoryStream(jpeg);

        JpegSegmentScanner.TryFindXmpPacket(stream, out _);

        // Everything after SOS is image data, so the scan must not have consumed it.
        Assert.True(stream.Position < stream.Length);
    }

    [Fact]
    public void ReportsIncompleteWhenTheDataIsTruncated()
    {
        byte[] jpeg = JpegBuilder.Build(xmpPacket: "<x:xmpmeta/>");
        byte[] truncated = jpeg.Take(8).ToArray();

        Assert.Equal(JpegScanOutcome.Incomplete,
            JpegSegmentScanner.TryFindXmpPacket(new MemoryStream(truncated), out _));
    }

    [Fact]
    public void ReportsNotJpegForOtherData()
    {
        byte[] notAJpeg = "definitely not a jpeg"u8.ToArray();

        Assert.Equal(JpegScanOutcome.NotJpeg,
            JpegSegmentScanner.TryFindXmpPacket(new MemoryStream(notAJpeg), out _));
    }

    [Fact]
    public void ReadsTheUploadStateStraightOutOfAJpeg()
    {
        IXmpMeta meta = CarsInBikeLanesXmp.Create(new XmpUploadState(true, DateTimeOffset.UtcNow, "submission-1"));
        string packet = XmpMetaFactory.SerializeToString(meta, null);
        byte[] jpeg = JpegBuilder.Build(xmpPacket: packet);

        XmpUploadState state = JpegSegmentScanner.ReadUploadState(new MemoryStream(jpeg));

        Assert.True(state.Uploaded);
        Assert.Equal("submission-1", state.SubmissionId);
    }

    [Fact]
    public void AJpegWithNoXmpReadsAsNotUploaded()
    {
        Assert.False(JpegSegmentScanner.ReadUploadState(new MemoryStream(JpegBuilder.Build())).Uploaded);
    }
}
