using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using XmpCore;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class XmpUploadStateTests
{
    [Fact]
    public void RoundTripsThroughASerializedPacket()
    {
        DateTimeOffset uploadedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        IXmpMeta written = CarsInBikeLanesXmp.Create(new XmpUploadState(true, uploadedAt, "abc123"));

        byte[] packet = XmpMetaFactory.SerializeToBuffer(written, null);
        XmpUploadState read = CarsInBikeLanesXmp.Read(CarsInBikeLanesXmp.TryParse(packet));

        Assert.True(read.Uploaded);
        Assert.Equal(uploadedAt, read.UploadedAt);
        Assert.Equal("abc123", read.SubmissionId);
    }

    [Fact]
    public void UsesARealNamespaceUriSoOtherToolsCanReadIt()
    {
        IXmpMeta meta = CarsInBikeLanesXmp.Create(XmpUploadState.UploadedNow(null));
        string packet = XmpMetaFactory.SerializeToString(meta, null);

        Assert.Contains(CarsInBikeLanesXmp.NamespaceUri, packet, StringComparison.Ordinal);
    }

    [Fact]
    public void APacketWithoutTheAppPropertiesIsNotUploaded()
    {
        IXmpMeta meta = XmpMetaFactory.Create();
        meta.SetProperty("http://purl.org/dc/elements/1.1/", "title", "unrelated");

        XmpUploadState state = CarsInBikeLanesXmp.Read(meta);

        Assert.False(state.Uploaded);
        Assert.Null(state.UploadedAt);
    }

    [Fact]
    public void ANullPacketIsNotUploaded()
    {
        Assert.False(CarsInBikeLanesXmp.Read(null).Uploaded);
    }

    [Fact]
    public void MalformedPacketsDoNotThrow()
    {
        Assert.Null(CarsInBikeLanesXmp.TryParse("this is not xmp"u8.ToArray()));
        Assert.Null(CarsInBikeLanesXmp.TryParse(Array.Empty<byte>()));
    }

    [Fact]
    public void WritingNotUploadedClearsThePreviousUploadDetails()
    {
        IXmpMeta meta = CarsInBikeLanesXmp.Create(new XmpUploadState(true, DateTimeOffset.UtcNow, "abc123"));

        CarsInBikeLanesXmp.Write(meta, XmpUploadState.NotUploaded);
        XmpUploadState state = CarsInBikeLanesXmp.Read(meta);

        Assert.False(state.Uploaded);
        Assert.Null(state.UploadedAt);
        Assert.Null(state.SubmissionId);
    }
}
