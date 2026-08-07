using idunno.AtProto;
using idunno.AtProto.Authentication;
using Microsoft.Extensions.Options;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Providers
{
    /// <summary>
    /// Creates configured atproto agents and resolves atproto identities.
    /// </summary>
    public class BlueskyOAuthProvider
    {
        /// <summary>
        /// Default service used before the user's real PDS is discovered. Every OAuth flow resolves
        /// the account's actual PDS and authorization server from its DID, so this is only a
        /// starting point.
        /// </summary>
        private static readonly Uri DefaultService = new Uri("https://bsky.social");

        private readonly BlueskyOAuthOptions options;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<BlueskyOAuthProvider> logger;

        public BlueskyOAuthProvider(IOptions<BlueskyOAuthOptions> options,
            ILoggerFactory loggerFactory,
            ILogger<BlueskyOAuthProvider> logger)
        {
            this.options = options.Value;
            this.loggerFactory = loggerFactory;
            this.logger = logger;
        }

        public BlueskyOAuthOptions Options => options;

        /// <summary>
        /// Creates an agent for a single OAuth operation. The caller owns it and must dispose it.
        /// </summary>
        public AtProtoAgent CreateAgent()
        {
            AtProtoAgentOptions agentOptions = new AtProtoAgentOptions()
            {
                LoggerFactory = loggerFactory,

                // We discard the Bluesky tokens as soon as identity is verified, so there is
                // nothing to keep refreshed in the background.
                EnableBackgroundTokenRefresh = false,

                OAuthOptions = new OAuthOptions()
                {
                    ClientId = options.ClientId,
                    ReturnUri = new Uri(options.RedirectUri),
                    Scopes = options.Scopes
                }
            };

            // Use the constructor that takes options directly. The IHttpClientFactory overload in
            // idunno.AtProto 3.1.0 never assigns Options, unlike every other overload, which leaves
            // Options null and makes BuildOAuth2LoginUri throw "OAuth options are not configured".
            // These calls only happen at sign in and at publish time, so an agent owned HttpClient
            // is fine; the agent is disposed by the caller.
            return new AtProtoAgent(DefaultService, agentOptions);
        }

        /// <summary>
        /// Resolves the current handle for a DID.
        /// </summary>
        /// <remarks>
        /// Handles can be reassigned, DIDs cannot, so the DID is what we store and the handle is
        /// looked up when it is actually needed. Returns null if the DID document declares no
        /// handle, in which case callers should fall back to whatever they have.
        /// </remarks>
        public virtual async Task<string?> ResolveHandleFromDid(string did, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(did))
            {
                return null;
            }

            try
            {
                using AtProtoAgent agent = CreateAgent();
                DidDocument? didDocument = await agent.ResolveDidDocument(new Did(did), cancellationToken);
                return GetHandleFromDidDocument(didDocument);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve handle for DID {Did}.", did);
                return null;
            }
        }

        /// <summary>
        /// Pulls the handle out of a DID document's alsoKnownAs entries.
        /// </summary>
        public static string? GetHandleFromDidDocument(DidDocument? didDocument)
        {
            string? atUri = didDocument?.AlsoKnownAs?
                .FirstOrDefault(aka => aka.StartsWith("at://", StringComparison.OrdinalIgnoreCase));

            if (atUri is null)
            {
                return null;
            }

            string handle = atUri["at://".Length..];
            return string.IsNullOrWhiteSpace(handle) ? null : handle;
        }

        /// <summary>
        /// Trims the decoration users tend to type around a handle.
        /// </summary>
        public static string NormalizeHandle(string handle)
        {
            handle = handle.Trim();

            if (handle.StartsWith("at://", StringComparison.OrdinalIgnoreCase))
            {
                handle = handle["at://".Length..];
            }

            if (handle.StartsWith('@'))
            {
                handle = handle[1..];
            }

            // People paste profile links.
            const string profilePrefix = "bsky.app/profile/";
            int profileIndex = handle.IndexOf(profilePrefix, StringComparison.OrdinalIgnoreCase);
            if (profileIndex >= 0)
            {
                handle = handle[(profileIndex + profilePrefix.Length)..];
            }

            return handle.TrimEnd('/').ToLowerInvariant();
        }
    }
}
