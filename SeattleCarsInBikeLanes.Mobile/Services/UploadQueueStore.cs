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
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The serialized <see cref="QueuedReportPayload"/>: which photos, and the report about them.
    /// </summary>
    [Column("payload")]
    public string Payload { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("state")]
    public int State { get; set; }

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
    /// Run at startup. A report recorded as uploading is one whose process went away while it was
    /// being sent, and it restarts from the beginning rather than from the finalize call, because
    /// the server throws away the blobs from an unfinished upload after ten minutes.
    /// </remarks>
    Task ResetInterruptedAsync();
}

/// <inheritdoc />
public sealed class UploadQueueStore : IUploadQueueStore
{
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
        await db.UpdateAsync(record);
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
            await created.CreateTableAsync<UploadQueueRecord>();
            connection = created;
            return connection;
        }
        finally
        {
            mutex.Release();
        }
    }
}
