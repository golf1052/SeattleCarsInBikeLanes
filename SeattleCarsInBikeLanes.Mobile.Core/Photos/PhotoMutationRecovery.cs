using System.Security.Cryptography;
using System.Text.Json;

namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

public interface IRecoverablePhotoTarget
{
    Task<string> GetIdentityAsync(string id, CancellationToken token);
    Task<byte[]> ReadAsync(string id, CancellationToken token);
    Task WriteAndSyncAsync(string id, byte[] bytes, CancellationToken token);
    Task SynchronizeAsync(string id, CancellationToken token);
}

/// <summary>
/// Serializes app access around an in-place library edit. The journal and two byte-complete copies
/// precede truncation; they describe only an unfinished mutation, never permanent photo history.
/// </summary>
public sealed class PhotoMutationRecovery(string root, IRecoverablePhotoTarget target,
    Action<string, Exception>? reportRecoveryFailure = null)
{
    private static readonly SemaphoreSlim Access = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, Exception> blocked = new(StringComparer.Ordinal);
    private sealed record Journal(string Id, string Identity, string OriginalHash, string UpdatedHash);

    public bool IsBlocked(string id) => blocked.ContainsKey(id);

    public async Task<T> WithRecoveredAccessAsync<T>(Func<Task<T>> action, CancellationToken token, string? id = null)
    {
        await Access.WaitAsync(token);
        try
        {
            await RecoverAsync(token);
            if (id is not null && blocked.TryGetValue(id, out Exception? error))
                throw new IOException("The photo has an unfinished recovery operation.", error);
            return await action();
        }
        finally
        {
            Access.Release();
        }
    }

    public Task WriteAsync(string id, Func<byte[], byte[]> update, CancellationToken token) =>
        WithRecoveredAccessAsync(async () =>
        {
            string identity = await target.GetIdentityAsync(id, token);
            byte[] original = await target.ReadAsync(id, token);
            byte[] updated = update(original);
            string operation = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(root);
            DurableFile.SyncDirectory(Path.GetDirectoryName(root)!);
            string directory = Path.Combine(root, operation);
            Directory.CreateDirectory(directory);
            DurableFile.SyncDirectory(root);
            await DurableFile.WriteAsync(Path.Combine(directory, "original"), original, token);
            await DurableFile.WriteAsync(Path.Combine(directory, "updated"), updated, token);
            Journal journal = new Journal(id, identity, Hash(original), Hash(updated));
            await DurableFile.WriteAsync(Path.Combine(directory, "journal.tmp"),
                JsonSerializer.SerializeToUtf8Bytes(journal), token);
            DurableFile.SyncDirectory(directory);
            File.Move(Path.Combine(directory, "journal.tmp"), Path.Combine(directory, "journal"));
            DurableFile.SyncDirectory(directory);

            if (await target.GetIdentityAsync(id, token) != identity ||
                Hash(await target.ReadAsync(id, token)) != journal.OriginalHash)
            {
                throw new IOException("The photo changed before its metadata could be saved.");
            }
            token.ThrowIfCancellationRequested();
            await target.WriteAndSyncAsync(id, updated, CancellationToken.None);
            await VerifyAndRetireAsync(directory, journal, CancellationToken.None);
            return true;
        }, token, id);

    private async Task RecoverAsync(CancellationToken token)
    {
        blocked.Clear();
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            {
                throw new InvalidDataException("Unrecognized photo recovery directory.");
            }
            string journalPath = Path.Combine(directory, "journal");
            if (!File.Exists(journalPath))
            {
                // No published journal means the target was never opened destructively, or the
                // verified journal was already retired. Neither case needs these orphan copies.
                Directory.Delete(directory, recursive: true);
                DurableFile.SyncDirectory(root);
                continue;
            }
            Journal journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllBytesAsync(journalPath, token))
                ?? throw new InvalidDataException("Photo recovery journal is empty.");
            try
            {
                await RecoverJournalAsync(directory, journal, token);
            }
            catch (IOException ex)
            {
                blocked[journal.Id] = ex;
                reportRecoveryFailure?.Invoke(journal.Id, ex);
            }
        }
    }

    private async Task RecoverJournalAsync(string directory, Journal journal, CancellationToken token)
    {
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(directory, "original"), token);
        byte[] updated = await File.ReadAllBytesAsync(Path.Combine(directory, "updated"), token);
        if (Hash(original) != journal.OriginalHash || Hash(updated) != journal.UpdatedHash)
        {
            throw new InvalidDataException("Photo recovery copies failed their integrity check.");
        }
        if (await target.GetIdentityAsync(journal.Id, token) != journal.Identity)
        {
            throw new IOException("The photo identity changed; its recovery copies were retained.");
        }
        byte[] current = await target.ReadAsync(journal.Id, token);
        if (Hash(current) != journal.UpdatedHash)
        {
            // A truncated write contains a prefix of staged bytes. Do not overwrite a different
            // complete external edit merely because a journal exists for this URI.
            if (Hash(current) != journal.OriginalHash ||
                current.Length == 0)
            {
                if (current.Length > updated.Length || !updated.AsSpan(0, current.Length).SequenceEqual(current))
                {
                    throw new IOException("The photo was edited outside the app; recovery needs attention.");
                }
            }
            await target.WriteAndSyncAsync(journal.Id, updated, CancellationToken.None);
        }
        await VerifyAndRetireAsync(directory, journal, CancellationToken.None);
    }

    private async Task VerifyAndRetireAsync(string directory, Journal journal, CancellationToken token)
    {
        // Matching bytes may only be in the OS cache after a killed writer. Synchronize the target
        // even when recovery did not have to rewrite it, before deleting the durable backup.
        await target.SynchronizeAsync(journal.Id, token);
        if (await target.GetIdentityAsync(journal.Id, token) != journal.Identity ||
            Hash(await target.ReadAsync(journal.Id, token)) != journal.UpdatedHash)
        {
            throw new IOException("The saved photo could not be verified; recovery copies were retained.");
        }
        File.Delete(Path.Combine(directory, "journal"));
        DurableFile.SyncDirectory(directory);
        Directory.Delete(directory, recursive: true);
        DurableFile.SyncDirectory(root);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
