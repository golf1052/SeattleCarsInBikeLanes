using SQLite;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// A photo the user imported from elsewhere in their library.
/// </summary>
[Table("imported_photos")]
public sealed class ImportedPhoto
{
    /// <summary>
    /// The Photos framework local identifier. The photo itself is never copied.
    /// </summary>
    [PrimaryKey]
    [Column("local_identifier")]
    public string LocalIdentifier { get; set; } = string.Empty;

    [Column("added_at")]
    public DateTime AddedAt { get; set; }

    [Column("submitted")]
    public bool Submitted { get; set; }

    [Column("submitted_at")]
    public DateTime? SubmittedAt { get; set; }

    [Column("submission_id")]
    public string? SubmissionId { get; set; }
}

/// <summary>
/// Tracks photos the user imported, and whether they have been submitted.
/// </summary>
/// <remarks>
/// For photos the app took, the submitted flag lives in the photo's own XMP and this store is not
/// involved. Imported photos need somewhere else to keep it: iOS only lets an app edit assets it
/// did not create by asking the user to confirm every single write, and a confirmation dialog after
/// every upload would be intolerable.
/// </remarks>
public interface IImportedPhotoStore
{
    Task<IReadOnlyList<ImportedPhoto>> GetAllAsync();

    Task AddAsync(IEnumerable<string> localIdentifiers);

    Task RemoveAsync(IEnumerable<string> localIdentifiers);

    Task MarkSubmittedAsync(IEnumerable<string> localIdentifiers, string? submissionId);

    Task ClearAsync();
}

/// <inheritdoc />
public sealed class ImportedPhotoStore : IImportedPhotoStore
{
    private readonly string databasePath;
    private readonly SemaphoreSlim mutex = new SemaphoreSlim(1, 1);
    private SQLiteAsyncConnection? connection;

    public ImportedPhotoStore(string databasePath)
    {
        this.databasePath = databasePath;
    }

    public async Task<IReadOnlyList<ImportedPhoto>> GetAllAsync()
    {
        SQLiteAsyncConnection db = await GetConnectionAsync();
        return await db.Table<ImportedPhoto>().OrderByDescending(photo => photo.AddedAt).ToListAsync();
    }

    public async Task AddAsync(IEnumerable<string> localIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(localIdentifiers);

        SQLiteAsyncConnection db = await GetConnectionAsync();
        DateTime now = DateTime.UtcNow;

        foreach (string identifier in localIdentifiers.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            // Re-importing a photo must not wipe the fact that it was already submitted.
            ImportedPhoto? existing = await db.FindAsync<ImportedPhoto>(identifier);
            if (existing is not null)
            {
                continue;
            }

            await db.InsertAsync(new ImportedPhoto()
            {
                LocalIdentifier = identifier,
                AddedAt = now,
                Submitted = false
            });
        }
    }

    public async Task RemoveAsync(IEnumerable<string> localIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(localIdentifiers);

        SQLiteAsyncConnection db = await GetConnectionAsync();
        foreach (string identifier in localIdentifiers)
        {
            await db.DeleteAsync<ImportedPhoto>(identifier);
        }
    }

    public async Task MarkSubmittedAsync(IEnumerable<string> localIdentifiers, string? submissionId)
    {
        ArgumentNullException.ThrowIfNull(localIdentifiers);

        SQLiteAsyncConnection db = await GetConnectionAsync();
        DateTime now = DateTime.UtcNow;

        foreach (string identifier in localIdentifiers)
        {
            ImportedPhoto? photo = await db.FindAsync<ImportedPhoto>(identifier);
            if (photo is null)
            {
                continue;
            }

            photo.Submitted = true;
            photo.SubmittedAt = now;
            photo.SubmissionId = submissionId;
            await db.UpdateAsync(photo);
        }
    }

    public async Task ClearAsync()
    {
        SQLiteAsyncConnection db = await GetConnectionAsync();
        await db.DeleteAllAsync<ImportedPhoto>();
    }

    private async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (connection is not null)
        {
            return connection;
        }

        await mutex.WaitAsync();
        try
        {
            if (connection is not null)
            {
                return connection;
            }

            SQLiteAsyncConnection created = new SQLiteAsyncConnection(databasePath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
            await created.CreateTableAsync<ImportedPhoto>();
            connection = created;
            return connection;
        }
        finally
        {
            mutex.Release();
        }
    }
}
