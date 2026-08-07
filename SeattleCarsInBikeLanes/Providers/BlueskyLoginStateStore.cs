using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Providers
{
    /// <summary>
    /// Carries the in flight OAuth authorization request across the redirect to Bluesky and back.
    /// </summary>
    /// <remarks>
    /// The state contains the PKCE code verifier and the DPoP proof key, so it is encrypted with
    /// data protection and stored in a short lived cookie. Keeping it in a cookie rather than a
    /// database means Bluesky sign in needs no persistent storage at all, unlike Mastodon which
    /// needs the mastodon-oauth-mapping container to remember per instance client registrations.
    /// </remarks>
    public class BlueskyLoginStateStore
    {
        private const string ProtectorPurpose = "SeattleCarsInBikeLanes.BlueskyLoginState";

        private readonly IDataProtector protector;
        private readonly ILogger<BlueskyLoginStateStore> logger;

        public BlueskyLoginStateStore(IDataProtectionProvider dataProtectionProvider,
            ILogger<BlueskyLoginStateStore> logger)
        {
            protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            this.logger = logger;
        }

        public void Store(HttpResponse response, BlueskyLoginState state, TimeSpan lifetime)
        {
            string payload = protector.Protect(JsonSerializer.Serialize(state));

            response.Cookies.Append(BlueskyAuthDefaults.LoginStateCookie, payload, new CookieOptions()
            {
                HttpOnly = true,
                Secure = response.HttpContext.Request.IsHttps,
                // Lax, not Strict. Strict would withhold the cookie on the redirect back from
                // Bluesky, which is a cross site navigation, and the callback would always fail.
                SameSite = SameSiteMode.Lax,
                MaxAge = lifetime,
                Path = "/"
            });
        }

        public BlueskyLoginState? Consume(HttpRequest request, HttpResponse response)
        {
            if (!request.Cookies.TryGetValue(BlueskyAuthDefaults.LoginStateCookie, out string? payload) ||
                string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            Clear(response);

            try
            {
                BlueskyLoginState? state = JsonSerializer.Deserialize<BlueskyLoginState>(protector.Unprotect(payload));
                if (state is null)
                {
                    return null;
                }

                if (DateTimeOffset.FromUnixTimeSeconds(state.ExpiresAt) < DateTimeOffset.UtcNow)
                {
                    logger.LogWarning("Bluesky login state expired before the callback arrived.");
                    return null;
                }

                return state;
            }
            catch (Exception ex)
            {
                // Tampered, or protected with a key that has since rolled.
                logger.LogWarning(ex, "Failed to read Bluesky login state cookie.");
                return null;
            }
        }

        public void Clear(HttpResponse response)
        {
            response.Cookies.Delete(BlueskyAuthDefaults.LoginStateCookie, new CookieOptions()
            {
                HttpOnly = true,
                Secure = response.HttpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/"
            });
        }
    }

    /// <param name="OAuthState">Serialized idunno.AtProto OAuthLoginState.</param>
    /// <param name="ExpectedDid">
    /// The DID we resolved from the handle before starting the flow. The spec requires us to
    /// confirm the DID the authorization server ultimately returns is the one we asked for,
    /// otherwise a hostile server could authenticate an account we never requested.
    /// </param>
    /// <param name="Handle">The handle the user typed, for error messages.</param>
    /// <param name="ExpiresAt">Unix seconds after which this request is abandoned.</param>
    public record BlueskyLoginState(string OAuthState, string ExpectedDid, string Handle, long ExpiresAt);
}
