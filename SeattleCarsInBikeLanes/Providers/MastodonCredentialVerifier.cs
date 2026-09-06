using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace SeattleCarsInBikeLanes.Providers;

public sealed record VerifiedMastodonAccount(string Server, string Id, string Username)
{
    public string FullUsername => $"@{Username}@{new Uri(Server).Host}";
}

public sealed class CredentialRejectedException : Exception;
public sealed class ProviderUnavailableException(string message) : Exception(message);

public sealed class MastodonCredentialVerifier(HttpClient client)
{
    private const int MaxIdentityBytes = 64 * 1024;
    public static string NormalizeServer(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Invalid Mastodon server.");
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public async Task<VerifiedMastodonAccount> VerifyAsync(string server, string token, CancellationToken cancellationToken)
    {
        string canonical = NormalizeServer(server);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
            $"{canonical}/api/v1/accounts/verify_credentials");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CredentialRejectedException();
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderUnavailableException("Mastodon could not verify the account. Try again shortly.");
        }
        if (response.Content.Headers.ContentLength > MaxIdentityBytes)
            throw new ProviderUnavailableException("Mastodon returned an oversized account response.");
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream body = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > MaxIdentityBytes)
                throw new ProviderUnavailableException("Mastodon returned an oversized account response.");
            body.Write(buffer, 0, count);
        }
        Account? account = JsonSerializer.Deserialize<Account>(body.ToArray());
        if (string.IsNullOrWhiteSpace(account?.Id) || string.IsNullOrWhiteSpace(account.Username))
        {
            throw new ProviderUnavailableException("Mastodon returned an incomplete account.");
        }
        return new VerifiedMastodonAccount(canonical, account.Id, account.Username);
    }

    private sealed record Account(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string Username);
}
