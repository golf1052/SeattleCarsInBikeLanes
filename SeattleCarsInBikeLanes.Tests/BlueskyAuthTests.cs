using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Tests
{
    public class BlueskyAuthTests
    {
        private static BlueskyTokenIssuer CreateIssuer()
        {
            return new BlueskyTokenIssuer(DataProtectionProvider.Create(nameof(BlueskyAuthTests)));
        }

        private static ClaimsPrincipal CreatePrincipal(string did, string handle)
        {
            return new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(BlueskyAuthDefaults.DidClaim, did),
                new Claim(BlueskyAuthDefaults.HandleClaim, handle)
            }, BlueskyAuthDefaults.BearerScheme));
        }

        [Fact]
        public void TokenIssuer_RoundTripsIdentity()
        {
            BlueskyTokenIssuer issuer = CreateIssuer();

            string token = issuer.Issue(CreatePrincipal("did:plc:abc123", "someone.bsky.social"),
                TimeSpan.FromDays(90));

            AuthenticationTicket? ticket = issuer.Validate(token);

            Assert.NotNull(ticket);
            Assert.Equal("did:plc:abc123", ticket.Principal.FindFirstValue(BlueskyAuthDefaults.DidClaim));
            Assert.Equal("someone.bsky.social", ticket.Principal.FindFirstValue(BlueskyAuthDefaults.HandleClaim));
        }

        [Fact]
        public void TokenIssuer_RejectsExpiredToken()
        {
            BlueskyTokenIssuer issuer = CreateIssuer();

            string token = issuer.Issue(CreatePrincipal("did:plc:abc123", "someone.bsky.social"),
                TimeSpan.FromSeconds(-1));

            Assert.Null(issuer.Validate(token));
        }

        [Fact]
        public void TokenIssuer_RejectsTamperedToken()
        {
            BlueskyTokenIssuer issuer = CreateIssuer();

            string token = issuer.Issue(CreatePrincipal("did:plc:abc123", "someone.bsky.social"),
                TimeSpan.FromDays(90));

            int middle = token.Length / 2;
            string tampered = token[..middle] +
                (token[middle] == 'A' ? 'B' : 'A') +
                token[(middle + 1)..];

            // Data protection detects the tampering and refuses to unprotect the payload.
            Assert.Null(issuer.Validate(tampered));
        }

        [Fact]
        public void TokenIssuer_RejectsTokenFromAnotherKeyRing()
        {
            string token = CreateIssuer().Issue(CreatePrincipal("did:plc:abc123", "someone.bsky.social"),
                TimeSpan.FromDays(90));

            BlueskyTokenIssuer otherIssuer =
                new BlueskyTokenIssuer(DataProtectionProvider.Create("SomeOtherApplication"));

            Assert.Null(otherIssuer.Validate(token));
        }

        [Theory]
        [InlineData("someone.bsky.social", "someone.bsky.social")]
        [InlineData("@someone.bsky.social", "someone.bsky.social")]
        [InlineData("  someone.bsky.social  ", "someone.bsky.social")]
        [InlineData("SomeOne.BSky.Social", "someone.bsky.social")]
        [InlineData("at://someone.bsky.social", "someone.bsky.social")]
        [InlineData("https://bsky.app/profile/someone.bsky.social", "someone.bsky.social")]
        [InlineData("bsky.app/profile/someone.bsky.social/", "someone.bsky.social")]
        public void NormalizeHandle_StripsDecoration(string input, string expected)
        {
            Assert.Equal(expected, BlueskyOAuthProvider.NormalizeHandle(input));
        }

        [Fact]
        public void GetHandleFromDidDocument_ReturnsNullWhenAbsent()
        {
            Assert.Null(BlueskyOAuthProvider.GetHandleFromDidDocument(null));
        }

        [Theory]
        [InlineData("https://seattle.carinbikelane.com/client-metadata.json", false)]
        [InlineData("http://localhost?redirect_uri=http%3A%2F%2F127.0.0.1%2Fblueskyredirect", true)]
        public void UsesLocalhostClientId_DetectsDevelopmentClient(string clientId, bool expected)
        {
            BlueskyOAuthOptions options = new BlueskyOAuthOptions() { ClientId = clientId };

            Assert.Equal(expected, options.UsesLocalhostClientId);
        }
    }
}
