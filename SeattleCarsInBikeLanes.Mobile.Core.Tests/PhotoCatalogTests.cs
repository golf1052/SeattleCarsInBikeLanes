using Microsoft.Extensions.Logging.Abstractions;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;
using SeattleCarsInBikeLanes.Mobile.Services;
using XmpPhotoTestContent = SeattleCarsInBikeLanes.Mobile.Core.Tests.PrivatePhotoStoreTests.XmpPhotoTestContent;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class PhotoCatalogTests
{
    private static readonly DateTimeOffset SubmittedAt =
        new DateTimeOffset(2026, 9, 5, 18, 24, 31, TimeSpan.Zero).AddTicks(1234567);

    [Theory]
    [InlineData(PhotoOrigin.Captured)]
    [InlineData(PhotoOrigin.Imported)]
    [InlineData(PhotoOrigin.PrivateCaptured)]
    [InlineData(PhotoOrigin.PrivateImported)]
    public async Task LoadsFullSubmissionMetadataForEveryOriginAndKeepsItOnRepeatedReads(PhotoOrigin origin)
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore imported = new ImportedPhotoStore(database.Path);
        FakePhotoLibrary library = new FakePhotoLibrary();
        FakePrivatePhotoStore privatePhotos = new FakePrivatePhotoStore();
        XmpUploadState expected = new XmpUploadState(true, SubmittedAt, "existing-report");
        string id;
        if (origin is PhotoOrigin.PrivateCaptured or PhotoOrigin.PrivateImported)
        {
            id = (await privatePhotos.SaveAsync(
                JpegWithState(expected), origin == PhotoOrigin.PrivateImported)).Id;
        }
        else
        {
            id = "system-photo";
            library.AddPhoto(id, origin == PhotoOrigin.Captured,
                origin == PhotoOrigin.Captured ? JpegWithState(expected) : XmpPhotoTestContent.BlankJpeg());
            if (origin == PhotoOrigin.Imported)
            {
                await imported.AddAsync(new[] { id });
                await imported.MarkSubmittedAsync(new[] { id }, expected.SubmissionId, expected.UploadedAt);
            }
        }

        PhotoCatalog catalog = CreateCatalog(library, privatePhotos, imported);
        ReportPhoto first = Assert.Single(await catalog.GetPhotosAsync());
        ReportPhoto second = Assert.Single(await catalog.GetPhotosAsync());

        Assert.Equal(id, first.Id);
        Assert.Equal(origin, first.Origin);
        Assert.Equal(origin, second.Origin);
        AssertState(expected, first);
        AssertState(expected, second);
        if (origin == PhotoOrigin.Captured)
        {
            Assert.Equal(1, library.MetadataReads);
        }
        else if (origin is PhotoOrigin.PrivateCaptured or PhotoOrigin.PrivateImported)
        {
            Assert.Equal(1, privatePhotos.MetadataReads);
        }
    }

    [Fact]
    public async Task MixedOriginReportSharesOneIdAndTimestampImmediatelyAndAfterRecreation()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore imported = new ImportedPhotoStore(database.Path);
        FakePhotoLibrary library = new FakePhotoLibrary();
        FakePrivatePhotoStore privatePhotos = new FakePrivatePhotoStore();
        library.AddPhoto("captured", true, XmpPhotoTestContent.BlankJpeg());
        library.AddPhoto("imported", false, XmpPhotoTestContent.BlankJpeg());
        await imported.AddAsync(new[] { "imported" });
        await privatePhotos.SaveAsync(XmpPhotoTestContent.BlankJpeg(), imported: false);
        await privatePhotos.SaveAsync(XmpPhotoTestContent.BlankJpeg(), imported: true);
        PhotoCatalog catalog = CreateCatalog(library, privatePhotos, imported);
        IReadOnlyList<ReportPhoto> before = await catalog.GetPhotosAsync();
        Assert.Equal(4, before.Count);
        Assert.All(before, photo => AssertState(XmpUploadState.NotUploaded, photo));
        DateTimeOffset earliest = DateTimeOffset.UtcNow;

        await catalog.MarkSubmittedAsync(before, "mixed-report");

        DateTimeOffset latest = DateTimeOffset.UtcNow;
        IReadOnlyList<ReportPhoto> immediate = await catalog.GetPhotosAsync();
        Assert.Equal(4, immediate.Count);
        DateTimeOffset timestamp = Assert.IsType<DateTimeOffset>(
            Assert.Single(immediate.Select(photo => photo.SubmittedAt).Distinct()));
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.InRange(timestamp, earliest, latest);
        XmpUploadState expected = new XmpUploadState(true, timestamp, "mixed-report");
        Assert.All(immediate, photo => AssertState(expected, photo));

        ImportedPhoto indexed = Assert.Single(await imported.GetAllAsync());
        Assert.True(indexed.Submitted);
        Assert.Equal(expected.SubmissionId, indexed.SubmissionId);
        Assert.Equal(timestamp.UtcTicks, indexed.SubmittedAt?.Ticks);
        Assert.Equal(expected, await library.ReadUploadStateAsync("captured"));
        foreach (ReportPhoto photo in immediate.Where(photo =>
                     photo.Origin is PhotoOrigin.PrivateCaptured or PhotoOrigin.PrivateImported))
        {
            Assert.Equal(expected, await privatePhotos.ReadUploadStateAsync(photo.Id));
        }

        await database.CloseConnectionAsync();
        PhotoCatalog reopened = CreateCatalog(
            new FakePhotoLibrary(library), new FakePrivatePhotoStore(privatePhotos),
            new ImportedPhotoStore(database.Path));
        IReadOnlyList<ReportPhoto> restored = await reopened.GetPhotosAsync();
        Assert.Equal(4, restored.Count);
        Assert.All(restored, photo =>
        {
            AssertState(expected, photo);
            Assert.Equal(Assert.Single(before, original => original.Id == photo.Id).Origin, photo.Origin);
        });
    }

    [Fact]
    public async Task CapturedMetadataWriteFailurePersistsInIndexAndDeduplicatesWithoutLosingMetadata()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore imported = new ImportedPhotoStore(database.Path);
        FakePhotoLibrary library = new FakePhotoLibrary();
        library.AddPhoto("captured", true, XmpPhotoTestContent.BlankJpeg());
        library.FailMetadataWrites = true;
        PhotoCatalog catalog = CreateCatalog(library, new FakePrivatePhotoStore(), imported);

        await catalog.MarkSubmittedAsync(await catalog.GetPhotosAsync(), "fallback-report");

        ReportPhoto immediate = Assert.Single(await catalog.GetPhotosAsync());
        Assert.Equal(PhotoOrigin.Captured, immediate.Origin);
        Assert.True(immediate.Submitted);
        Assert.Equal("fallback-report", immediate.SubmissionId);
        DateTimeOffset timestamp = Assert.IsType<DateTimeOffset>(immediate.SubmittedAt);
        XmpUploadState expected = new XmpUploadState(true, timestamp, "fallback-report");
        ImportedPhoto indexed = Assert.Single(await imported.GetAllAsync());
        Assert.Equal("captured", indexed.LocalIdentifier);
        Assert.True(indexed.Submitted);
        Assert.Equal(expected.SubmissionId, indexed.SubmissionId);
        Assert.Equal(timestamp.UtcTicks, indexed.SubmittedAt?.Ticks);
        Assert.Equal(XmpUploadState.NotUploaded, await library.ReadUploadStateAsync("captured"));

        await database.CloseConnectionAsync();
        PhotoCatalog reopened = CreateCatalog(new FakePhotoLibrary(library), new FakePrivatePhotoStore(),
            new ImportedPhotoStore(database.Path));
        ReportPhoto restored = Assert.Single(await reopened.GetPhotosAsync());
        Assert.Equal("captured", restored.Id);
        Assert.Equal(PhotoOrigin.Captured, restored.Origin);
        AssertState(expected, restored);
        AssertState(expected, Assert.Single(await reopened.GetPhotosAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RereportedCaptureUsesNewestXmpOrFallbackIndexAfterRestart(bool latestWriteFails)
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore imported = new ImportedPhotoStore(database.Path);
        FakePhotoLibrary library = new FakePhotoLibrary();
        XmpUploadState oldState = new XmpUploadState(true, DateTimeOffset.UtcNow.AddDays(-1), "old-report");
        library.AddPhoto("captured", true,
            latestWriteFails ? JpegWithState(oldState) : XmpPhotoTestContent.BlankJpeg());
        await imported.AddAsync(new[] { "captured" });
        await imported.MarkSubmittedAsync(new[] { "captured" }, oldState.SubmissionId, oldState.UploadedAt);
        PhotoCatalog catalog = CreateCatalog(library, new FakePrivatePhotoStore(), imported);
        IReadOnlyList<ReportPhoto> original = await catalog.GetPhotosAsync();
        AssertState(oldState, Assert.Single(original));
        library.FailMetadataWrites = latestWriteFails;

        await catalog.MarkSubmittedAsync(original, "new-report");

        ReportPhoto immediate = Assert.Single(await catalog.GetPhotosAsync());
        DateTimeOffset timestamp = Assert.IsType<DateTimeOffset>(immediate.SubmittedAt);
        Assert.True(timestamp > oldState.UploadedAt);
        XmpUploadState expected = new XmpUploadState(true, timestamp, "new-report");
        AssertState(expected, immediate);
        Assert.Equal(PhotoOrigin.Captured, immediate.Origin);
        ImportedPhoto indexed = Assert.Single(await imported.GetAllAsync());
        XmpUploadState expectedIndex = latestWriteFails ? expected : oldState;
        Assert.Equal(expectedIndex.SubmissionId, indexed.SubmissionId);
        Assert.Equal(expectedIndex.UploadedAt?.UtcTicks, indexed.SubmittedAt?.Ticks);
        Assert.Equal(latestWriteFails ? oldState : expected,
            await library.ReadUploadStateAsync("captured"));

        await database.CloseConnectionAsync();
        FakePhotoLibrary reopenedLibrary = new FakePhotoLibrary(library);
        PhotoCatalog reopened = CreateCatalog(reopenedLibrary, new FakePrivatePhotoStore(),
            new ImportedPhotoStore(database.Path));
        ReportPhoto restored = Assert.Single(await reopened.GetPhotosAsync());
        Assert.Equal("captured", restored.Id);
        Assert.Equal(PhotoOrigin.Captured, restored.Origin);
        AssertState(expected, restored);
        AssertState(expected, Assert.Single(await reopened.GetPhotosAsync()));
        Assert.Equal(1, reopenedLibrary.MetadataReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimestampTieBetweenXmpAndIndexRetainsTheKnownSubmissionId(bool xmpHasId)
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore imported = new ImportedPhotoStore(database.Path);
        FakePhotoLibrary library = new FakePhotoLibrary();
        XmpUploadState expected = new XmpUploadState(true, SubmittedAt, "known-report");
        library.AddPhoto("captured", true,
            JpegWithState(expected with { SubmissionId = xmpHasId ? expected.SubmissionId : null }));
        await imported.AddAsync(new[] { "captured" });
        await imported.MarkSubmittedAsync(new[] { "captured" },
            xmpHasId ? null : expected.SubmissionId, SubmittedAt);
        await database.CloseConnectionAsync();

        PhotoCatalog catalog = CreateCatalog(new FakePhotoLibrary(library), new FakePrivatePhotoStore(),
            new ImportedPhotoStore(database.Path));

        AssertState(expected, Assert.Single(await catalog.GetPhotosAsync()));
        AssertState(expected, Assert.Single(await catalog.GetPhotosAsync()));
    }

    [Fact]
    public async Task ReimportingSubmittedSystemPhotoPreservesIndexMetadataWithoutSubmittingAgain()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore imported = new ImportedPhotoStore(database.Path);
        FakePhotoLibrary library = new FakePhotoLibrary();
        library.AddPhoto("imported", false, XmpPhotoTestContent.BlankJpeg());
        await imported.AddAsync(new[] { "imported" });
        XmpUploadState expected = new XmpUploadState(true, SubmittedAt, "original-report");
        await imported.MarkSubmittedAsync(new[] { "imported" }, expected.SubmissionId, expected.UploadedAt);
        await database.CloseConnectionAsync();

        ImportedPhotoStore reopenedStore = new ImportedPhotoStore(database.Path);
        library.PickedPhotos = new[] { new PickedPhoto("imported", null) };
        PhotoCatalog catalog = CreateCatalog(library, new FakePrivatePhotoStore(), reopenedStore);
        ReportPhoto reimported = Assert.Single(await catalog.ImportPhotosAsync(1));

        Assert.Equal(PhotoOrigin.Imported, reimported.Origin);
        AssertState(expected, reimported);
        AssertState(expected, Assert.Single(await catalog.GetPhotosAsync()));
        AssertState(expected, Assert.Single(await catalog.ImportPhotosAsync(1)));
        ImportedPhoto indexed = Assert.Single(await reopenedStore.GetAllAsync());
        Assert.Equal(expected.SubmissionId, indexed.SubmissionId);
        Assert.Equal(SubmittedAt.UtcDateTime, indexed.SubmittedAt);
    }

    [Fact]
    public async Task PrivateImportedJpegRetainsItsExistingSubmissionMetadata()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        FakePhotoLibrary library = new FakePhotoLibrary();
        FakePrivatePhotoStore privatePhotos = new FakePrivatePhotoStore();
        XmpUploadState expected = new XmpUploadState(true, SubmittedAt, "exported-private-report");
        library.Access = PhotoLibraryAccess.Denied;
        library.PickedPhotos = new[] { new PickedPhoto(null, JpegWithState(expected)) };
        PhotoCatalog catalog = CreateCatalog(library, privatePhotos, new ImportedPhotoStore(database.Path));

        ReportPhoto imported = Assert.Single(await catalog.ImportPhotosAsync(1));

        Assert.Equal(PhotoOrigin.PrivateImported, imported.Origin);
        AssertState(expected, imported);
        AssertState(expected, Assert.Single(await catalog.GetPhotosAsync()));
        Assert.Equal(expected, await privatePhotos.ReadUploadStateAsync(imported.Id));

        FakePhotoLibrary reopenedLibrary = new FakePhotoLibrary(library) { Access = PhotoLibraryAccess.Denied };
        PhotoCatalog reopened = CreateCatalog(reopenedLibrary, new FakePrivatePhotoStore(privatePhotos),
            new ImportedPhotoStore(database.Path));
        ReportPhoto restored = Assert.Single(await reopened.GetPhotosAsync());
        Assert.Equal(imported.Id, restored.Id);
        Assert.Equal(PhotoOrigin.PrivateImported, restored.Origin);
        AssertState(expected, restored);
    }

    [Fact]
    public async Task SqliteUnspecifiedTimestampIsInterpretedAsUtc()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore imported = new ImportedPhotoStore(database.Path);
        FakePhotoLibrary library = new FakePhotoLibrary();
        library.AddPhoto("imported", false, XmpPhotoTestContent.BlankJpeg());
        await imported.AddAsync(new[] { "imported" });
        DateTimeOffset timestamp = new DateTimeOffset(2026, 9, 5, 8, 15, 22, TimeSpan.FromHours(-7));
        await imported.MarkSubmittedAsync(new[] { "imported" }, "utc-report", timestamp);
        DateTime stored = Assert.IsType<DateTime>(Assert.Single(await imported.GetAllAsync()).SubmittedAt);
        Assert.Equal(DateTimeKind.Unspecified, stored.Kind);

        PhotoCatalog catalog = CreateCatalog(library, new FakePrivatePhotoStore(), imported);
        ReportPhoto photo = Assert.Single(await catalog.GetPhotosAsync());

        AssertState(new XmpUploadState(true, timestamp.ToUniversalTime(), "utc-report"), photo);
        DateTimeOffset actual = Assert.IsType<DateTimeOffset>(photo.SubmittedAt);
        Assert.Equal(TimeSpan.Zero, actual.Offset);
        Assert.Equal(stored.Ticks, actual.UtcTicks);
    }

    [Fact]
    public async Task ExportedAndReimportedXmpMetadataWorksWithAnUnsubmittedIndex()
    {
        using ImportedPhotoTestDatabase sourceDatabase = new ImportedPhotoTestDatabase();
        FakePhotoLibrary sourceLibrary = new FakePhotoLibrary();
        sourceLibrary.AddPhoto("captured", true, XmpPhotoTestContent.BlankJpeg());
        PhotoCatalog source = CreateCatalog(sourceLibrary, new FakePrivatePhotoStore(),
            new ImportedPhotoStore(sourceDatabase.Path));
        await source.MarkSubmittedAsync(await source.GetPhotosAsync(), "exported-report");
        ReportPhoto submitted = Assert.Single(await source.GetPhotosAsync());
        byte[] exportedJpeg = Assert.IsType<byte[]>(await source.GetPhotoDataAsync(submitted.Id));
        XmpUploadState expected = new XmpUploadState(true, submitted.SubmittedAt, submitted.SubmissionId);

        using ImportedPhotoTestDatabase destinationDatabase = new ImportedPhotoTestDatabase();
        ImportedPhotoStore imported = new ImportedPhotoStore(destinationDatabase.Path);
        FakePhotoLibrary destinationLibrary = new FakePhotoLibrary();
        destinationLibrary.AddPhoto("reimported", false, exportedJpeg);
        destinationLibrary.PickedPhotos = new[] { new PickedPhoto("reimported", null) };
        PhotoCatalog destination = CreateCatalog(destinationLibrary, new FakePrivatePhotoStore(), imported);

        ReportPhoto reimported = Assert.Single(await destination.ImportPhotosAsync(1));

        Assert.Equal(PhotoOrigin.Imported, reimported.Origin);
        AssertState(expected, reimported);
        ImportedPhoto record = Assert.Single(await imported.GetAllAsync());
        Assert.False(record.Submitted);
        Assert.Null(record.SubmissionId);
        Assert.Null(record.SubmittedAt);
        AssertState(expected, Assert.Single(await destination.GetPhotosAsync()));
        Assert.Equal(1, destinationLibrary.MetadataReads);

        await destinationDatabase.CloseConnectionAsync();
        PhotoCatalog reopened = CreateCatalog(new FakePhotoLibrary(destinationLibrary), new FakePrivatePhotoStore(),
            new ImportedPhotoStore(destinationDatabase.Path));
        AssertState(expected, Assert.Single(await reopened.GetPhotosAsync()));
    }

    [Theory]
    [InlineData(PhotoLibraryAccess.Granted, PhotoOrigin.Captured)]
    [InlineData(PhotoLibraryAccess.Denied, PhotoOrigin.PrivateCaptured)]
    public async Task NewlyCapturedPhotosRemainUnsubmitted(PhotoLibraryAccess access, PhotoOrigin origin)
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        FakePhotoLibrary library = new FakePhotoLibrary() { Access = access };
        FakePrivatePhotoStore privatePhotos = new FakePrivatePhotoStore();
        PhotoCatalog catalog = CreateCatalog(library, privatePhotos, new ImportedPhotoStore(database.Path));

        ReportPhoto captured = Assert.IsType<ReportPhoto>(
            await catalog.AddCapturedPhotoAsync(XmpPhotoTestContent.BlankJpeg()));

        Assert.Equal(origin, captured.Origin);
        AssertState(XmpUploadState.NotUploaded, captured);
        AssertState(XmpUploadState.NotUploaded, Assert.Single(await catalog.GetPhotosAsync()));
        PhotoCatalog reopened = CreateCatalog(new FakePhotoLibrary(library) { Access = access },
            new FakePrivatePhotoStore(privatePhotos), new ImportedPhotoStore(database.Path));
        ReportPhoto restored = Assert.Single(await reopened.GetPhotosAsync());
        Assert.Equal(captured.Id, restored.Id);
        Assert.Equal(origin, restored.Origin);
        AssertState(XmpUploadState.NotUploaded, restored);
    }

    private static PhotoCatalog CreateCatalog(FakePhotoLibrary library, FakePrivatePhotoStore privatePhotos,
        IImportedPhotoStore imported) =>
        new PhotoCatalog(library, privatePhotos, imported, new PassthroughImageResizer(),
            NullLogger<PhotoCatalog>.Instance);

    private static byte[] JpegWithState(XmpUploadState state) =>
        new XmpPhotoTestContent().SetUploadState(XmpPhotoTestContent.BlankJpeg(), state);

    private static void AssertState(XmpUploadState expected, IReportedPhoto actual)
    {
        Assert.Equal(expected.Uploaded, actual.Submitted);
        Assert.Equal(expected.SubmissionId, actual.SubmissionId);
        Assert.Equal(expected.UploadedAt, actual.SubmittedAt);
    }

    private sealed class FakePhotoLibrary : IPhotoLibraryService
    {
        private readonly Dictionary<string, LibraryPhoto> photos;
        private readonly XmpPhotoTestContent content = new XmpPhotoTestContent();

        public FakePhotoLibrary(FakePhotoLibrary? previous = null)
        {
            photos = previous?.photos ?? new Dictionary<string, LibraryPhoto>(StringComparer.Ordinal);
        }

        public PhotoLibraryAccess Access { get; set; } = PhotoLibraryAccess.Granted;
        public IReadOnlyList<PickedPhoto> PickedPhotos { get; set; } = Array.Empty<PickedPhoto>();
        public bool FailMetadataWrites { get; set; }
        public int MetadataReads { get; private set; }
        public bool SupportsWritingUploadState => !FailMetadataWrites;
        public bool ConfirmsCapturedPhotoDeletion => false;

        public void AddPhoto(string id, bool captured, byte[] jpeg) =>
            photos.Add(id, new LibraryPhoto(new PhotoAsset(id, null, null), captured, jpeg.ToArray()));

        public Task<PhotoLibraryAccess> CheckAccessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Access);

        public Task<PhotoLibraryAccess> RequestAccessAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> SaveCapturedPhotoAsync(byte[] jpeg, CancellationToken cancellationToken = default)
        {
            string id = $"captured-{Guid.NewGuid():N}";
            AddPhoto(id, true, jpeg);
            return Task.FromResult<string?>(id);
        }

        public Task<IReadOnlyList<PhotoAsset>> GetCapturedPhotosAsync(int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhotoAsset>>(
                photos.Values.Where(photo => photo.Captured).Take(limit).Select(photo => photo.Asset).ToList());

        public Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhotoAsset>>(
                ids.Where(photos.ContainsKey).Select(id => photos[id].Asset).ToList());

        public Task<byte[]?> GetThumbnailAsync(string id, int pixelSize,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]?> GetPhotoDataAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(photos[id].Jpeg.ToArray());

        public Task<XmpUploadState> ReadUploadStateAsync(string id, CancellationToken cancellationToken = default)
        {
            MetadataReads++;
            return Task.FromResult(content.ReadUploadState(photos[id].Jpeg));
        }

        public Task<bool> WriteUploadStateAsync(string id, XmpUploadState state,
            CancellationToken cancellationToken = default)
        {
            if (FailMetadataWrites)
            {
                return Task.FromResult(false);
            }

            LibraryPhoto photo = photos[id];
            photos[id] = photo with { Jpeg = content.SetUploadState(photo.Jpeg, state) };
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<PickedPhoto>> PickPhotosAsync(int limit,
            CancellationToken cancellationToken = default) => Task.FromResult(PickedPhotos);

        public Task ReleasePhotoAccessAsync(IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeletePhotosAsync(IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed record LibraryPhoto(PhotoAsset Asset, bool Captured, byte[] Jpeg);
    }

    private sealed class FakePrivatePhotoStore : IPrivatePhotoStore
    {
        private readonly Dictionary<string, StoredPhoto> photos;
        private readonly XmpPhotoTestContent content = new XmpPhotoTestContent();

        public FakePrivatePhotoStore(FakePrivatePhotoStore? previous = null)
        {
            photos = previous?.photos ?? new Dictionary<string, StoredPhoto>(StringComparer.Ordinal);
        }

        public int MetadataReads { get; private set; }

        public Task<IReadOnlyList<PrivatePhotoAsset>> GetPhotosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PrivatePhotoAsset>>(photos.Values.Select(photo => photo.Asset).ToList());

        public Task<PrivatePhotoAsset> SaveAsync(byte[] jpeg, bool imported,
            CancellationToken cancellationToken = default)
        {
            string id = $"private:{(imported ? "imported" : "captured")}:{Guid.NewGuid():N}.jpg";
            PrivatePhotoAsset asset = new PrivatePhotoAsset(id, imported, null, null);
            photos.Add(id, new StoredPhoto(asset, jpeg.ToArray()));
            return Task.FromResult(asset);
        }

        public Task<byte[]?> GetDataAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(photos[id].Jpeg.ToArray());

        public Task<XmpUploadState> ReadUploadStateAsync(string id, CancellationToken cancellationToken = default)
        {
            MetadataReads++;
            return Task.FromResult(content.ReadUploadState(photos[id].Jpeg));
        }

        public Task<bool> WriteUploadStateAsync(string id, XmpUploadState state,
            CancellationToken cancellationToken = default)
        {
            StoredPhoto photo = photos[id];
            photos[id] = photo with { Jpeg = content.SetUploadState(photo.Jpeg, state) };
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private sealed record StoredPhoto(PrivatePhotoAsset Asset, byte[] Jpeg);
    }
}
