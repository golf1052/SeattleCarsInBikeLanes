using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;
using System.Text;
using XmpCore;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class PrivatePhotoStoreTests : IDisposable
{
    private readonly string root =
        Path.Combine(AppContext.BaseDirectory, $"private-photos-{Guid.NewGuid():N}");

    [Fact]
    public async Task PhotosSurviveStoreRecreation()
    {
        GeoPosition location = new GeoPosition(47.6062, -122.3321);
        DateTime takenAt = new DateTime(2026, 8, 17, 9, 30, 0);
        FakeContent content = new FakeContent(new PhotoExifData(takenAt, location));
        PrivatePhotoStore first = new PrivatePhotoStore(root, content);

        PrivatePhotoAsset saved = await first.SaveAsync(new byte[] { 0, 2, 3 }, imported: false);

        PrivatePhotoStore reopened = new PrivatePhotoStore(root, content);
        IReadOnlyList<PrivatePhotoAsset> photos = await reopened.GetPhotosAsync();

        PrivatePhotoAsset photo = Assert.Single(photos);
        Assert.Equal(saved.Id, photo.Id);
        Assert.False(photo.Imported);
        Assert.Equal(location, photo.Location);
        Assert.Equal(takenAt, photo.CreatedAt?.DateTime);
        Assert.Equal(new byte[] { 0, 2, 3 }, await reopened.GetDataAsync(saved.Id));
    }

    [Fact]
    public async Task KeepsImportedPhotosSeparate()
    {
        PrivatePhotoStore store = new PrivatePhotoStore(root, new FakeContent(PhotoExifData.Empty));

        PrivatePhotoAsset captured = await store.SaveAsync(new byte[] { 0, 1 }, imported: false);
        PrivatePhotoAsset imported = await store.SaveAsync(new byte[] { 0, 2 }, imported: true);

        IReadOnlyList<PrivatePhotoAsset> photos = await store.GetPhotosAsync();

        Assert.Contains(photos, photo => photo.Id == captured.Id && !photo.Imported);
        Assert.Contains(photos, photo => photo.Id == imported.Id && photo.Imported);
    }

    [Fact]
    public async Task UploadStateReplacementSurvivesStoreRecreation()
    {
        FakeContent content = new FakeContent(PhotoExifData.Empty);
        PrivatePhotoStore store = new PrivatePhotoStore(root, content);
        PrivatePhotoAsset saved = await store.SaveAsync(new byte[] { 0, 9 }, imported: false);
        XmpUploadState state = XmpUploadState.UploadedNow("submission-1");
        string path = Assert.Single(Directory.GetFiles(Path.Combine(root, "captured"), "*.jpg"));
        await File.WriteAllBytesAsync(path + ".tmp", [8]);

        Assert.True(await store.WriteUploadStateAsync(saved.Id, state));

        PrivatePhotoStore reopened = new PrivatePhotoStore(root, content);
        XmpUploadState read = await reopened.ReadUploadStateAsync(saved.Id);
        Assert.True(read.Uploaded);
        Assert.Equal(new byte[] { 1, 9 }, await reopened.GetDataAsync(saved.Id));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveCreatesAndReusesNestedDirectories(bool imported)
    {
        string nestedRoot = Path.Combine(root, "nested", "photos");
        PrivatePhotoStore store = new PrivatePhotoStore(nestedRoot, new FakeContent(PhotoExifData.Empty));

        PrivatePhotoAsset first = await store.SaveAsync([0, 1], imported);
        PrivatePhotoAsset second = await store.SaveAsync([0, 2], imported);

        PrivatePhotoStore reopened = new PrivatePhotoStore(nestedRoot, new FakeContent(PhotoExifData.Empty));
        Assert.Equal(new byte[] { 0, 1 }, await reopened.GetDataAsync(first.Id));
        Assert.Equal(new byte[] { 0, 2 }, await reopened.GetDataAsync(second.Id));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DirectoryCreationFailurePropagatesAndAllowsRetry()
    {
        Directory.CreateDirectory(root);
        string captured = Path.Combine(root, "captured");
        await File.WriteAllTextAsync(captured, "not a directory");
        PrivatePhotoStore store = new PrivatePhotoStore(root, new FakeContent(PhotoExifData.Empty));

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([0, 1], imported: false));
        Assert.Equal("not a directory", await File.ReadAllTextAsync(captured));

        File.Delete(captured);
        PrivatePhotoAsset saved = await store.SaveAsync([0, 1], imported: false);
        Assert.Equal(new byte[] { 0, 1 }, await store.GetDataAsync(saved.Id));
    }

    [Fact]
    public async Task FailedMetadataReplacementPreservesOriginalAndAllowsRetry()
    {
        PrivatePhotoStore store = new PrivatePhotoStore(root, new FakeContent(PhotoExifData.Empty));
        PrivatePhotoAsset saved = await store.SaveAsync([0, 9], imported: false);
        string path = Assert.Single(Directory.GetFiles(Path.Combine(root, "captured"), "*.jpg"));
        Directory.CreateDirectory(path + ".tmp");
        XmpUploadState state = XmpUploadState.UploadedNow("submission-1");

        Exception? failure = await Record.ExceptionAsync(() => store.WriteUploadStateAsync(saved.Id, state));
        Assert.True(failure is IOException or UnauthorizedAccessException,
            failure?.ToString() ?? "Expected the temporary file creation to fail.");
        Assert.Equal(new byte[] { 0, 9 }, await store.GetDataAsync(saved.Id));
        Assert.False((await store.ReadUploadStateAsync(saved.Id)).Uploaded);

        Directory.Delete(path + ".tmp");
        Assert.True(await store.WriteUploadStateAsync(saved.Id, state));
        Assert.Equal(new byte[] { 1, 9 }, await store.GetDataAsync(saved.Id));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullSubmissionMetadataSurvivesStoreAndContentRecreation(bool imported)
    {
        PrivatePhotoStore store = new PrivatePhotoStore(root, new XmpPhotoTestContent());
        PrivatePhotoAsset saved = await store.SaveAsync(XmpPhotoTestContent.BlankJpeg(), imported);
        DateTimeOffset submittedAt = new DateTimeOffset(2026, 9, 5, 18, 24, 31, TimeSpan.Zero)
            .AddTicks(1234567);
        XmpUploadState expected = new XmpUploadState(true, submittedAt, "private-report-123");

        Assert.True(await store.WriteUploadStateAsync(saved.Id, expected));

        PrivatePhotoStore reopened = new PrivatePhotoStore(root, new XmpPhotoTestContent());
        PrivatePhotoAsset photo = Assert.Single(await reopened.GetPhotosAsync());
        Assert.Equal(saved.Id, photo.Id);
        Assert.Equal(imported, photo.Imported);
        Assert.Equal(expected, await reopened.ReadUploadStateAsync(photo.Id));
        byte[] persisted = Assert.IsType<byte[]>(await reopened.GetDataAsync(photo.Id));
        using MemoryStream jpeg = new MemoryStream(persisted);
        Assert.Equal(expected, JpegSegmentScanner.ReadUploadState(jpeg));
    }

    [Fact]
    public async Task DeleteRemovesThePersistentFile()
    {
        PrivatePhotoStore store = new PrivatePhotoStore(root, new FakeContent(PhotoExifData.Empty));
        PrivatePhotoAsset saved = await store.SaveAsync(new byte[] { 0, 1 }, imported: false);

        Assert.True(await store.DeleteAsync(saved.Id));

        Assert.Null(await store.GetDataAsync(saved.Id));
        Assert.Empty(await store.GetPhotosAsync());
    }

    [Fact]
    public async Task RejectsIdsThatEscapeThePhotoDirectory()
    {
        PrivatePhotoStore store = new PrivatePhotoStore(root, new FakeContent(PhotoExifData.Empty));
        string id = $"private:captured:../outside/{Guid.NewGuid():N}.jpg";

        Assert.Null(await store.GetDataAsync(id));
        Assert.False(await store.DeleteAsync(id));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal sealed class XmpPhotoTestContent : IPrivatePhotoContent
    {
        public static byte[] BlankJpeg() =>
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestFiles", "blank.jpg"));

        public PhotoExifData ReadExif(byte[] jpeg) => PhotoExifData.Empty;

        public XmpUploadState ReadUploadState(byte[] jpeg)
        {
            using MemoryStream stream = new MemoryStream(jpeg);
            return JpegSegmentScanner.ReadUploadState(stream);
        }

        public byte[] SetUploadState(byte[] jpeg, XmpUploadState state)
        {
            byte[] signature = Encoding.ASCII.GetBytes(JpegSegmentScanner.XmpSignature);
            byte[] packet = XmpMetaFactory.SerializeToBuffer(CarsInBikeLanesXmp.Create(state), null);
            int length = signature.Length + packet.Length + 2;
            using MemoryStream output = new MemoryStream();
            output.Write(jpeg, 0, 2);
            output.Write(new byte[] { 0xff, 0xe1, (byte)(length >> 8), (byte)length });
            output.Write(signature);
            output.Write(packet);
            output.Write(jpeg, 2, jpeg.Length - 2);
            return output.ToArray();
        }
    }

    private sealed class FakeContent : IPrivatePhotoContent
    {
        private readonly PhotoExifData exif;

        public FakeContent(PhotoExifData exif)
        {
            this.exif = exif;
        }

        public PhotoExifData ReadExif(byte[] jpeg) => exif;

        public XmpUploadState ReadUploadState(byte[] jpeg) =>
            jpeg.Length > 0 && jpeg[0] == 1
                ? new XmpUploadState(true, null, null)
                : XmpUploadState.NotUploaded;

        public byte[] SetUploadState(byte[] jpeg, XmpUploadState state)
        {
            byte[] updated = jpeg.ToArray();
            updated[0] = state.Uploaded ? (byte)1 : (byte)0;
            return updated;
        }
    }
}
