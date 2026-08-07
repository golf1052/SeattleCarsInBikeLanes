namespace SeattleCarsInBikeLanes.Models
{
    /// <summary>
    /// Authentication scheme names and claim types for Bluesky sign in.
    /// </summary>
    public static class BlueskyAuthDefaults
    {
        /// <summary>
        /// Cookie scheme used by the web app.
        /// </summary>
        public const string CookieScheme = "Bluesky";

        /// <summary>
        /// Bearer token scheme, for the mobile app.
        /// </summary>
        public const string BearerScheme = "BlueskyBearer";

        /// <summary>
        /// Accepts either a cookie or a bearer token.
        /// </summary>
        public const string AnyScheme = "BlueskyAny";

        /// <summary>
        /// The user's decentralized identifier. Permanent, and the identity of record.
        /// </summary>
        public const string DidClaim = "bsky:did";

        /// <summary>
        /// The user's handle at the time they signed in. Can change, so it is re-resolved from the
        /// DID when it actually matters.
        /// </summary>
        public const string HandleClaim = "bsky:handle";

        /// <summary>
        /// Cookie holding the in flight authorization request state.
        /// </summary>
        public const string LoginStateCookie = "bsky_login";

        /// <summary>
        /// Cookie holding the signed in session.
        /// </summary>
        public const string SessionCookie = "bsky_session";
    }
}
