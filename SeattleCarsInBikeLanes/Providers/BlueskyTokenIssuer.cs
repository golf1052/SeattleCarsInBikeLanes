using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Providers
{
    /// <summary>
    /// Issues and validates bearer tokens asserting a verified Bluesky identity.
    /// </summary>
    /// <remarks>
    /// The web app uses a cookie, but native clients such as the mobile app cannot. This issues the
    /// same authentication ticket in a form that can travel in an Authorization header.
    ///
    /// The token is a data protection payload rather than a signed JWT. Nothing but this server
    /// ever needs to read it, and this way there is no signing key to provision in Key Vault or
    /// rotate. Data protection already gives us tamper detection and key management.
    /// </remarks>
    public class BlueskyTokenIssuer
    {
        private const string ProtectorPurpose = "SeattleCarsInBikeLanes.BlueskyBearerToken";

        private readonly ISecureDataFormat<AuthenticationTicket> ticketFormat;

        public BlueskyTokenIssuer(IDataProtectionProvider dataProtectionProvider)
        {
            ticketFormat = new TicketDataFormat(dataProtectionProvider.CreateProtector(ProtectorPurpose));
        }

        public string Issue(ClaimsPrincipal principal, TimeSpan lifetime)
        {
            AuthenticationProperties properties = new AuthenticationProperties()
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(lifetime)
            };

            return ticketFormat.Protect(new AuthenticationTicket(principal,
                properties,
                BlueskyAuthDefaults.BearerScheme));
        }

        public AuthenticationTicket? Validate(string token)
        {
            AuthenticationTicket? ticket = ticketFormat.Unprotect(token);
            if (ticket is null)
            {
                return null;
            }

            DateTimeOffset? expiresUtc = ticket.Properties.ExpiresUtc;
            if (expiresUtc is null || expiresUtc < DateTimeOffset.UtcNow)
            {
                return null;
            }

            return ticket;
        }
    }

    /// <summary>
    /// Reads a Bluesky bearer token from the Authorization header.
    /// </summary>
    public class BlueskyBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private const string Prefix = "Bearer ";

        private readonly BlueskyTokenIssuer tokenIssuer;

        public BlueskyBearerAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory loggerFactory,
            UrlEncoder encoder,
            BlueskyTokenIssuer tokenIssuer) : base(options, loggerFactory, encoder)
        {
            this.tokenIssuer = tokenIssuer;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? header = Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            string token = header[Prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            AuthenticationTicket? ticket;
            try
            {
                ticket = tokenIssuer.Validate(token);
            }
            catch (Exception)
            {
                // Tampered, or protected with a key that has since rolled.
                return Task.FromResult(AuthenticateResult.Fail("Invalid Bluesky bearer token."));
            }

            if (ticket is null)
            {
                return Task.FromResult(AuthenticateResult.Fail("Expired or invalid Bluesky bearer token."));
            }

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
