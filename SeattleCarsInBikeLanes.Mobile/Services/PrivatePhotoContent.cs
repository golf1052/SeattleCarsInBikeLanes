using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Services;

public sealed class PrivatePhotoContent : IPrivatePhotoContent
{
    public PhotoExifData ReadExif(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        return PhotoExif.Read(new MemoryStream(jpeg, writable: false));
    }

    public XmpUploadState ReadUploadState(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        return JpegSegmentScanner.ReadUploadState(new MemoryStream(jpeg, writable: false));
    }

    public byte[] SetUploadState(byte[] jpeg, XmpUploadState state) =>
        JpegXmpEditor.SetUploadState(jpeg, state);
}
