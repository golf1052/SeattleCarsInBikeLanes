namespace SeattleCarsInBikeLanes.Models
{
    /// <summary>
    /// Configuration for the Bluesky (atproto) OAuth client.
    /// </summary>
    /// <remarks>
    /// We are a public client. The atproto spec reserves confidential clients for apps that need
    /// long lived sessions; we discard the Bluesky tokens seconds after login, so there is nothing
    /// for a client authentication key to protect. See https://atproto.com/specs/oauth.
    /// </remarks>
    public class BlueskyOAuthOptions
    {
        public const string SectionName = "BlueskyOAuth";

        /// <summary>
        /// The OAuth client id. In production this is the fully qualified URL the client metadata
        /// document is served from, and must match that URL exactly. In development this uses the
        /// spec's <c>http://localhost</c> exception, where the authorization server synthesizes a
        /// virtual client metadata document from the query string instead of fetching one.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// The URL the authorization server redirects back to once the user has approved.
        /// </summary>
        public string RedirectUri { get; set; } = string.Empty;

        /// <summary>
        /// Homepage for the client. Must share a hostname with <see cref="ClientId"/>.
        /// </summary>
        public string ClientUri { get; set; } = string.Empty;

        /// <summary>
        /// Human readable client name, shown on the authorization screen for trusted clients.
        /// </summary>
        public string ClientName { get; set; } = "Seattle Cars in Bike Lanes";

        /// <summary>
        /// Where to send the browser once login completes.
        /// </summary>
        public string PostLoginRedirect { get; set; } = "/";

        /// <summary>
        /// How long the site keeps a user signed in. Independent of the Bluesky session, which we
        /// throw away immediately after verifying identity.
        /// </summary>
        public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(90);

        /// <summary>
        /// How long an in flight authorization request stays valid.
        /// </summary>
        public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Scopes requested. We only authenticate identity, so <c>atproto</c> alone is enough and a
        /// leaked token would grant no access to the user's repository.
        /// </summary>
        public string[] Scopes { get; set; } = new[] { "atproto" };

        /// <summary>
        /// True when running against the spec's localhost development exception, in which case we
        /// must not serve a client metadata document.
        /// </summary>
        public bool UsesLocalhostClientId =>
            ClientId.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
    }
}
