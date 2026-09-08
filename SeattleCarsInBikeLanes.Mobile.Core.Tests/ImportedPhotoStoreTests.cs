using SeattleCarsInBikeLanes.Mobile.Services;
using SQLite;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class ImportedPhotoStoreTests
{
    [Fact]
    public async Task SubmissionMetadataPersistsAcrossStoreAndConnectionRecreation()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore store = new ImportedPhotoStore(database.Path);
        await store.AddAsync(new[] { "first", "second" });
        DateTimeOffset submittedAt = new DateTimeOffset(2026, 9, 5, 18, 24, 31, TimeSpan.Zero)
            .AddTicks(1234567);

        await store.MarkSubmittedAsync(new[] { "first", "second" }, "report-123", submittedAt);
        await database.CloseConnectionAsync();

        ImportedPhotoStore reopened = new ImportedPhotoStore(database.Path);
        IReadOnlyList<ImportedPhoto> photos = await reopened.GetAllAsync();
        Assert.Equal(2, photos.Count);
        Assert.All(photos, photo =>
        {
            Assert.True(photo.Submitted);
            Assert.Equal("report-123", photo.SubmissionId);
            Assert.Equal(submittedAt.UtcDateTime, photo.SubmittedAt);
        });
    }

    [Fact]
    public async Task ExplicitOffsetTimestampIsStoredAsUtcForEveryPhoto()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore store = new ImportedPhotoStore(database.Path);
        await store.AddAsync(new[] { "first", "second" });
        DateTimeOffset submittedAt = new DateTimeOffset(2026, 9, 5, 8, 15, 22, TimeSpan.FromHours(-7))
            .AddTicks(7654321);

        await store.MarkSubmittedAsync(new[] { "first", "second" }, "shared-report", submittedAt);

        IReadOnlyList<ImportedPhoto> photos = await store.GetAllAsync();
        Assert.Equal(2, photos.Count);
        Assert.All(photos, photo =>
        {
            Assert.True(photo.Submitted);
            Assert.Equal("shared-report", photo.SubmissionId);
            DateTime stored = Assert.IsType<DateTime>(photo.SubmittedAt);
            Assert.Equal(submittedAt.UtcTicks, stored.Ticks);
            Assert.Equal(DateTimeKind.Unspecified, stored.Kind);
        });
    }

    [Fact]
    public async Task OmittedTimestampUsesOneCurrentUtcInstantForTheBatch()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore store = new ImportedPhotoStore(database.Path);
        await store.AddAsync(new[] { "first", "second" });
        DateTime before = DateTime.UtcNow;

        await store.MarkSubmittedAsync(new[] { "first", "second" }, "default-time");

        DateTime after = DateTime.UtcNow;
        IReadOnlyList<ImportedPhoto> photos = await store.GetAllAsync();
        Assert.Equal(2, photos.Count);
        DateTime sharedTime = Assert.IsType<DateTime>(Assert.Single(
            photos.Select(photo => photo.SubmittedAt).Distinct()));
        Assert.InRange(sharedTime.Ticks, before.Ticks, after.Ticks);
        Assert.All(photos, photo =>
        {
            Assert.True(photo.Submitted);
            Assert.Equal("default-time", photo.SubmissionId);
        });
    }

    [Fact]
    public async Task ReimportPreservesExistingSubmissionAndDoesNotSubmitNewPhotos()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore store = new ImportedPhotoStore(database.Path);
        await store.AddAsync(new[] { "submitted" });
        DateTime addedAt = Assert.Single(await store.GetAllAsync()).AddedAt;
        DateTimeOffset submittedAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        await store.MarkSubmittedAsync(new[] { "submitted" }, "original-report", submittedAt);
        await database.CloseConnectionAsync();

        ImportedPhotoStore reopened = new ImportedPhotoStore(database.Path);
        await reopened.AddAsync(new[] { "submitted", "new" });

        IReadOnlyList<ImportedPhoto> photos = await reopened.GetAllAsync();
        Assert.Equal(2, photos.Count);
        ImportedPhoto submitted = Assert.Single(photos, photo => photo.LocalIdentifier == "submitted");
        Assert.True(submitted.Submitted);
        Assert.Equal("original-report", submitted.SubmissionId);
        Assert.Equal(submittedAt.UtcDateTime, submitted.SubmittedAt);
        Assert.Equal(addedAt, submitted.AddedAt);
        ImportedPhoto unsubmitted = Assert.Single(photos, photo => photo.LocalIdentifier == "new");
        Assert.False(unsubmitted.Submitted);
        Assert.Null(unsubmitted.SubmittedAt);
        Assert.Null(unsubmitted.SubmissionId);
    }

    [Fact]
    public async Task SubmissionWithoutAnIdStillPersistsTheTimestamp()
    {
        using ImportedPhotoTestDatabase database = new ImportedPhotoTestDatabase();
        ImportedPhotoStore store = new ImportedPhotoStore(database.Path);
        await store.AddAsync(new[] { "photo" });
        DateTimeOffset submittedAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

        await store.MarkSubmittedAsync(new[] { "photo" }, null, submittedAt);
        await database.CloseConnectionAsync();

        ImportedPhoto photo = Assert.Single(await new ImportedPhotoStore(database.Path).GetAllAsync());
        Assert.True(photo.Submitted);
        Assert.Null(photo.SubmissionId);
        Assert.Equal(submittedAt.UtcDateTime, photo.SubmittedAt);
    }
}

internal sealed class ImportedPhotoTestDatabase : IDisposable
{
    private readonly string directory =
        System.IO.Path.Combine(AppContext.BaseDirectory, $"imported-photos-{Guid.NewGuid():N}");

    public ImportedPhotoTestDatabase()
    {
        SQLitePCL.Batteries_V2.Init();
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "photos.db3");
    }

    public string Path { get; }

    public Task CloseConnectionAsync() => new SQLiteAsyncConnection(
        Path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache).CloseAsync();

    public void Dispose()
    {
        // ImportedPhotoStore uses SQLite's shared pool but does not expose a close operation.
        CloseConnectionAsync().GetAwaiter().GetResult();
        Directory.Delete(directory, recursive: true);
    }
}
