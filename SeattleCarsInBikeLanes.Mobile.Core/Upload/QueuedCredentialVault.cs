using System.Text.Json;

namespace SeattleCarsInBikeLanes.Mobile.Core.Upload;

/// <summary>
/// A single secure value is both the snapshot store and its enumerable reference registry.
/// Queue rows contain only opaque references. Orphans are reconciled after a successful queue load.
/// </summary>
public sealed class QueuedCredentialVault(ISecureValueStore storage)
{
    private const string Key = "cbl.queued-credentials.v1";
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    public sealed record Entry(AccountSession Session, HashSet<string> Reports);

    public async Task<string> RetainAsync(string reportId, AccountSession session)
    {
        await gate.WaitAsync();
        try
        {
            Dictionary<string, Entry> entries = await ReadAsync();
            string reference = entries.FirstOrDefault(pair => pair.Value.Session == session).Key
                ?? Guid.NewGuid().ToString("N");
            if (!entries.TryGetValue(reference, out Entry? entry))
            {
                entry = new Entry(session, []);
                entries.Add(reference, entry);
            }
            entry.Reports.Add(reportId);
            await WriteAsync(entries);
            return reference;
        }
        finally { gate.Release(); }
    }

    public async Task<AccountSession> ResolveAsync(string reportId, string reference)
    {
        await gate.WaitAsync();
        try
        {
            Dictionary<string, Entry> entries = await ReadAsync();
            return entries.TryGetValue(reference, out Entry? entry) && entry.Reports.Contains(reportId)
                ? entry.Session : throw new IOException("The queued report's secure credentials are unavailable.");
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseAsync(string reportId)
    {
        await gate.WaitAsync();
        try
        {
            Dictionary<string, Entry> entries = await ReadAsync();
            foreach (Entry entry in entries.Values) entry.Reports.Remove(reportId);
            await WriteAsync(entries.Where(pair => pair.Value.Reports.Count != 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        finally { gate.Release(); }
    }

    public async Task ReconcileAsync(IReadOnlySet<string> reportsNeedingCredentials)
    {
        await gate.WaitAsync();
        try
        {
            Dictionary<string, Entry> entries = await ReadAsync();
            foreach (Entry entry in entries.Values)
                entry.Reports.IntersectWith(reportsNeedingCredentials);
            await WriteAsync(entries.Where(pair => pair.Value.Reports.Count != 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        finally { gate.Release(); }
    }

    private async Task<Dictionary<string, Entry>> ReadAsync()
    {
        string? value = await storage.GetAsync(Key);
        return value is null ? [] : JsonSerializer.Deserialize<Dictionary<string, Entry>>(value)
            ?? throw new InvalidDataException("The queued credential vault is unreadable.");
    }

    private Task WriteAsync(Dictionary<string, Entry> entries) =>
        storage.SetAsync(Key, JsonSerializer.Serialize(entries));
}
