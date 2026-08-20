using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;
using SQLite;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class UploadQueueStoreTests
{
    private static readonly string[] ExpectedColumns =
    {
        "attempts",
        "created_at",
        "id",
        "last_error",
        "next_attempt_at",
        "payload",
        "server_directed_retry",
        "state"
    };

    static UploadQueueStoreTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task FreshDatabaseHasCurrentExactSchema()
    {
        using TestDatabase database = new TestDatabase();

        await using (UploadQueueStore store = new UploadQueueStore(database.Path))
        {
            Assert.Empty(await store.GetAllAsync());
        }

        await AssertCurrentSchemaAsync(database.Path);
    }

    [Fact]
    public async Task LegacyDatabaseIsRecreatedWithoutRows()
    {
        using TestDatabase database = new TestDatabase();
        await ExecuteAsync(database.Path,
            """
            CREATE TABLE upload_queue (
                id TEXT PRIMARY KEY,
                payload TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                state INTEGER NOT NULL,
                attempts INTEGER NOT NULL,
                last_error TEXT,
                next_attempt_at INTEGER
            )
            """,
            """
            INSERT INTO upload_queue
                (id, payload, created_at, state, attempts)
            VALUES
                ('legacy', '{}', 0, 0, 0)
            """);

        await using (UploadQueueStore store = new UploadQueueStore(database.Path))
        {
            Assert.Empty(await store.GetAllAsync());
        }

        await AssertCurrentSchemaAsync(database.Path);
    }

    [Fact]
    public async Task PreReleaseExtraColumnsAreRemoved()
    {
        using TestDatabase database = new TestDatabase();
        await ExecuteAsync(database.Path,
            """
            CREATE TABLE upload_queue (
                id TEXT PRIMARY KEY,
                payload TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                state INTEGER NOT NULL,
                attempts INTEGER NOT NULL,
                last_error TEXT,
                next_attempt_at INTEGER,
                server_directed_retry INTEGER NOT NULL,
                obsolete_retry_field TEXT
            )
            """);

        await using (UploadQueueStore store = new UploadQueueStore(database.Path))
        {
            Assert.Empty(await store.GetAllAsync());
        }

        await AssertCurrentSchemaAsync(database.Path);
    }

    [Fact]
    public async Task CurrentDatabasePreservesRows()
    {
        using TestDatabase database = new TestDatabase();
        UploadQueueRecord expected = new UploadQueueRecord()
        {
            Id = "current",
            Payload = "{}",
            CreatedAt = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc),
            State = (int)UploadQueueState.Pending,
            Attempts = 2,
            LastError = "Waiting",
            NextAttemptAt = new DateTime(2026, 8, 19, 12, 5, 0, DateTimeKind.Utc),
            ServerDirectedRetry = true
        };

        await using (UploadQueueStore store = new UploadQueueStore(database.Path))
        {
            await store.AddAsync(expected);
        }

        await using (UploadQueueStore reopened = new UploadQueueStore(database.Path))
        {
            UploadQueueRecord actual = Assert.Single(await reopened.GetAllAsync());
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Payload, actual.Payload);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.Equal(expected.State, actual.State);
            Assert.Equal(expected.Attempts, actual.Attempts);
            Assert.Equal(expected.LastError, actual.LastError);
            Assert.Equal(expected.NextAttemptAt, actual.NextAttemptAt);
            Assert.True(actual.ServerDirectedRetry);
        }
    }

    [Fact]
    public async Task CurrentVersionWithWrongColumnsIsRejected()
    {
        using TestDatabase database = new TestDatabase();
        await using (UploadQueueStore store = new UploadQueueStore(database.Path))
        {
            Assert.Empty(await store.GetAllAsync());
        }

        await ExecuteAsync(database.Path, "ALTER TABLE upload_queue ADD COLUMN obsolete TEXT");

        await using UploadQueueStore reopened = new UploadQueueStore(database.Path);
        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.GetAllAsync());
        Assert.Contains("obsolete", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterruptedUploadClearsRetrySchedule()
    {
        using TestDatabase database = new TestDatabase();
        DateTime nextAttemptAt = DateTime.UtcNow.AddMinutes(5);

        await using UploadQueueStore store = new UploadQueueStore(database.Path);
        await store.AddAsync(new UploadQueueRecord()
        {
            Id = "interrupted",
            Payload = "{}",
            CreatedAt = DateTime.UtcNow,
            State = (int)UploadQueueState.Uploading,
            Attempts = 1,
            NextAttemptAt = nextAttemptAt,
            ServerDirectedRetry = true
        });

        await store.ResetInterruptedAsync();

        UploadQueueRecord record = Assert.Single(await store.GetAllAsync());
        Assert.Equal((int)UploadQueueState.Pending, record.State);
        Assert.Null(record.NextAttemptAt);
        Assert.False(record.ServerDirectedRetry);
    }

    private static async Task AssertCurrentSchemaAsync(string path)
    {
        SQLiteAsyncConnection connection = Open(path);
        try
        {
            Assert.Equal(
                UploadQueueStore.SchemaVersion,
                await connection.ExecuteScalarAsync<int>("PRAGMA user_version"));

            List<TableInfoRow> columns =
                await connection.QueryAsync<TableInfoRow>("PRAGMA table_info(upload_queue)");
            Assert.Equal(ExpectedColumns, columns.Select(column => column.Name).Order(StringComparer.Ordinal));

            HashSet<string> nullable = new HashSet<string>(StringComparer.Ordinal)
            {
                "last_error",
                "next_attempt_at"
            };
            foreach (TableInfoRow column in columns)
            {
                Assert.Equal(nullable.Contains(column.Name) ? 0 : 1, column.NotNull);
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task ExecuteAsync(string path, params string[] commands)
    {
        SQLiteAsyncConnection connection = Open(path);
        try
        {
            foreach (string command in commands)
            {
                await connection.ExecuteAsync(command);
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static SQLiteAsyncConnection Open(string path) => new SQLiteAsyncConnection(
        path,
        SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);

    private sealed class TableInfoRow
    {
        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("notnull")]
        public int NotNull { get; set; }
    }

    private sealed class TestDatabase : IDisposable
    {
        public TestDatabase()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"seattle-cars-upload-queue-{Guid.NewGuid():N}.db3");
        }

        public string Path { get; }

        public void Dispose()
        {
            DeleteIfPresent(Path);
            DeleteIfPresent($"{Path}-shm");
            DeleteIfPresent($"{Path}-wal");
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
