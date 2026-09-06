using SeattleCarsInBikeLanes.Core.Contracts;

namespace SeattleCarsInBikeLanes.Mobile.Core.Upload;

public interface ISecureValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    void Remove(string key);
}

public interface IClientDispatcher
{
    void Dispatch(Action action);
}

public sealed record AccountCredential(
    string AccountId, string DisplayName, string Token,
    string? Server = null, DateTimeOffset? ExpiresAt = null);

public sealed record AccountSession(AccountCredential? Bluesky = null, AccountCredential? Mastodon = null,
    bool BlueskySignedOut = false, bool MastodonSignedOut = false)
{
    public ReportAttribution Attribution => new ReportAttribution(Bluesky?.AccountId, Mastodon?.Server, Mastodon?.AccountId);
}

public enum CredentialCheckState { Valid, Invalid, Unavailable }
public sealed record CredentialCheck(CredentialCheckState State, AccountCredential? Credential = null);

public sealed record QueuedAttribution(ReportAttribution Intent, string? CredentialReference = null);
