using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SQLite;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// A report waiting to be sent, as it is written to disk.
/// </summary>
[Table("upload_queue")]
public sealed class UploadQueueRecord
{
    [PrimaryKey]
    [NotNull]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The serialized <see cref="QueuedReportPayload"/>: which photos, and the report about them.
    /// </summary>
    [NotNull]
    [Column("payload")]
    public string Payload { get; set; } = string.Empty;

    [NotNull]
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [NotNull]
    [Column("state")]
    public int State { get; set; }

    [NotNull]
    [Column("attempts")]
    public int Attempts { get; set; }

    /// <summary>
    /// What the last attempt said, in the server's own words where there were any.
    /// </summary>
    [Column("last_error")]
    public string? LastError { get; set; }

    /// <summary>
    /// When the next attempt is allowed, for a report backing off after a transient failure.
    /// </summary>
    [Column("next_attempt_at")]
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// Whether connectivity changes must preserve the server's requested retry time.
    /// </summary>
    [NotNull]
    [Column("server_directed_retry")]
    public bool ServerDirectedRetry { get; set; }
}

/// <summary>
/// Where reports wait between the user submitting them and the site accepting them.
/// </summary>
/// <remarks>
/// Kept on disk rather than in memory because the point of the queue is to survive the things that
/// end a process: the user swiping the app away, iOS reclaiming it while it is in the background, a
/// crash. A report the user believes they have sent must not quietly cease to exist.
/// </remarks>
public interface IUploadQueueStore
{
    Task<IReadOnlyList<UploadQueueRecord>> GetAllAsync();

    Task AddAsync(UploadQueueRecord record);

    Task UpdateAsync(UploadQueueRecord record);

    Task RemoveAsync(string id);

    /// <summary>
    /// Puts anything left mid flight back in the queue.
    /// </summary>
    /// <remarks>
    /// Only resets scheduling state. The payload's network-attempt/receipt phase is preserved so
    /// restart reconciles uncertain acceptance or resumes local-only acknowledgement.
    /// </remarks>
    Task ResetInterruptedAsync();
}

/// <inheritdoc />
public sealed class UploadQueueStore : IUploadQueueStore, IAsyncDisposable
{
    // The app is still pre-release, so older development schemas are intentionally discarded rather
    // than migrated. Once a public build exists, version changes must preserve queued reports.
    internal const int SchemaVersion = 2;

    private static readonly HashSet<string> ExpectedColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "id",
        "payload",
        "created_at",
        "state",
        "attempts",
        "last_error",
        "next_attempt_at",
        "server_directed_retry"
    };

    private readonly string databasePath;
    private readonly SemaphoreSlim mutex = new SemaphoreSlim(1, 1);
    private SQLiteAsyncConnection? connection;

    public UploadQueueStore(string databasePath)
    {
        this.databasePath = databasePath;
    }

    public async Task<IReadOnlyList<UploadQueueRecord>> GetAllAsync()
    {
        SQLiteAsyncConnection db = await GetConnectionAsync();
        return await db.Table<UploadQueueRecord>().OrderBy(record => record.CreatedAt).ToListAsync();
    }

    public async Task AddAsync(UploadQueueRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        SQLiteAsyncConnection db = await GetConnectionAsync();
        await db.InsertAsync(record);
    }

    public async Task UpdateAsync(UploadQueueRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        SQLiteAsyncConnection db = await GetConnectionAsync();
        if (await db.UpdateAsync(record) != 1)
            throw new IOException("The queued report could not be updated.");
    }

    public async Task RemoveAsync(string id)
    {
        SQLiteAsyncConnection db = await GetConnectionAsync();
        await db.DeleteAsync<UploadQueueRecord>(id);
    }

    public async Task ResetInterruptedAsync()
    {
        SQLiteAsyncConnection db = await GetConnectionAsync();
        await db.ExecuteAsync(
            "UPDATE upload_queue SET state = ?, next_attempt_at = NULL, server_directed_retry = 0 WHERE state = ?",
            (int)UploadQueueState.Pending,
            (int)UploadQueueState.Uploading);
    }

    public async ValueTask DisposeAsync()
    {
        await mutex.WaitAsync();
        try
        {
            if (connection is not null)
            {
                await connection.CloseAsync();
                connection = null;
            }
        }
        finally
        {
            mutex.Release();
        }
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
            try
            {
                await InitializeAsync(created);
                connection = created;
                return connection;
            }
            catch
            {
                await created.CloseAsync();
                throw;
            }
        }
        finally
        {
            mutex.Release();
        }
    }

    private static async Task InitializeAsync(SQLiteAsyncConnection db)
    {
        int version = await db.ExecuteScalarAsync<int>("PRAGMA user_version");
        if (version != SchemaVersion)
        {
            await db.RunInTransactionAsync(connection =>
            {
                connection.Execute("DROP TABLE IF EXISTS upload_queue");
                connection.CreateTable<UploadQueueRecord>();
                connection.Execute($"PRAGMA user_version = {SchemaVersion}");
            });
            return;
        }

        List<SQLiteConnection.ColumnInfo> columns = await db.GetTableInfoAsync("upload_queue");
        HashSet<string> actualColumns = columns.Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!ExpectedColumns.SetEquals(actualColumns))
        {
            string actual = string.Join(", ", actualColumns.Order(StringComparer.Ordinal));
            string expected = string.Join(", ", ExpectedColumns.Order(StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Upload queue schema version {SchemaVersion} has columns [{actual}]; expected [{expected}].");
        }
    }
}
