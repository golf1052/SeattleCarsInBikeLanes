using System.Security.Claims;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Controllers
{
    /// <summary>
    /// Sign in with Bluesky, using the atproto profile of OAuth.
    /// </summary>
    /// <remarks>
    /// The server is the OAuth client. The browser never sees a Bluesky token, which is the whole
    /// point of the rewrite: the previous implementation exported the DPoP private key out of the
    /// browser and posted it to us with every report.
    ///
    /// We only authenticate identity. Once the DID is verified the Bluesky session is revoked and
    /// forgotten, and the user carries our own cookie instead.
    /// </remarks>
    [ApiController]
    public class BlueskyAuthController : ControllerBase
    {
        private readonly ILogger<BlueskyAuthController> logger;
        private readonly BlueskyOAuthProvider oAuthProvider;
        private readonly BlueskyLoginStateStore loginStateStore;
        private readonly BlueskyTokenIssuer tokenIssuer;
        private readonly BlueskyOAuthOptions options;

        public BlueskyAuthController(ILogger<BlueskyAuthController> logger,
            BlueskyOAuthProvider oAuthProvider,
            BlueskyLoginStateStore loginStateStore,
            BlueskyTokenIssuer tokenIssuer)
        {
            this.logger = logger;
            this.oAuthProvider = oAuthProvider;
            this.loginStateStore = loginStateStore;
            this.tokenIssuer = tokenIssuer;
            options = oAuthProvider.Options;
        }

        /// <summary>
        /// The client metadata document. Authorization servers fetch this during the authorization
        /// request, and its URL is our client id.
        /// </summary>
        [HttpGet("/client-metadata.json")]
        [AllowAnonymous]
        public IActionResult GetClientMetadata()
        {
            if (options.UsesLocalhostClientId)
            {
                // Under the localhost development exception the authorization server synthesizes
                // the metadata from the client id query string and never fetches a document.
                return NotFound();
            }

            return new JsonResult(new Dictionary<string, object>()
            {
                ["client_id"] = options.ClientId,
                ["client_name"] = options.ClientName,
                ["client_uri"] = options.ClientUri,
                ["redirect_uris"] = new[] { options.RedirectUri },
                ["scope"] = string.Join(' ', options.Scopes),
                // We never refresh, but the token endpoint only returns a refresh token when the
                // grant is declared, and idunno.AtProto requires one to be present.
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                ["application_type"] = "web",
                ["token_endpoint_auth_method"] = "none",
                ["dpop_bound_access_tokens"] = true
            })
            {
                ContentType = "application/json"
            };
        }

        /// <summary>
        /// Starts an authorization request for a handle.
        /// </summary>
        [HttpPost("/api/BlueskyAuth/login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request?.Handle))
            {
                return BadRequest(new ErrorResponse("Enter your Bluesky handle."));
            }

            string handle = BlueskyOAuthProvider.NormalizeHandle(request.Handle);
            if (!Handle.TryParse(handle, out Handle? parsedHandle) || parsedHandle is null)
            {
                return BadRequest(new ErrorResponse($"\"{request.Handle}\" is not a valid Bluesky handle."));
            }

            try
            {
                using AtProtoAgent agent = oAuthProvider.CreateAgent();

                // Resolve up front so we can tell the user their handle is wrong before bouncing
                // them to Bluesky, and so we have a DID to compare against when they come back.
                Did? expectedDid = await agent.ResolveHandle(parsedHandle, cancellationToken);
                if (expectedDid is null)
                {
                    return BadRequest(new ErrorResponse($"Couldn't find a Bluesky account for \"{handle}\"."));
                }

                OAuthClient oAuthClient = agent.CreateOAuthClient();

                Uri authUrl = await agent.BuildOAuth2LoginUri(
                    oAuthClient: oAuthClient,
                    handle: parsedHandle,
                    returnUri: new Uri(options.RedirectUri),
                    cancellationToken: cancellationToken);

                if (oAuthClient.State is null)
                {
                    logger.LogError("Bluesky OAuth client had no state after building the login URI.");
                    return StatusCode(StatusCodes.Status502BadGateway,
                        new ErrorResponse("Couldn't start Bluesky login. Try again."));
                }

                loginStateStore.Store(Response,
                    new BlueskyLoginState(oAuthClient.State.ToJson(),
                        expectedDid.ToString(),
                        handle,
                        DateTimeOffset.UtcNow.Add(options.LoginTimeout).ToUnixTimeSeconds()),
                    options.LoginTimeout);

                return Ok(new LoginResponse(authUrl.ToString()));
            }
            catch (OAuthException ex)
            {
                logger.LogWarning(ex, "Failed to start Bluesky login for {Handle}.", handle);
                return BadRequest(new ErrorResponse("Couldn't start Bluesky login. Check your handle and try again."));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure starting Bluesky login for {Handle}.", handle);
                return StatusCode(StatusCodes.Status502BadGateway,
                    new ErrorResponse("Couldn't reach Bluesky. Try again shortly."));
            }
        }

        /// <summary>
        /// Where Bluesky sends the user once they have approved or denied the request.
        /// </summary>
        [HttpGet("/blueskyredirect")]
        [AllowAnonymous]
        public async Task<IActionResult> Callback(CancellationToken cancellationToken)
        {
            BlueskyLoginState? loginState = loginStateStore.Consume(Request, Response);
            if (loginState is null)
            {
                return LoginFailed("Your Bluesky login expired. Try again.");
            }

            if (Request.Query.TryGetValue("error", out var error))
            {
                logger.LogInformation("Bluesky login was not approved: {Error}", error.ToString());
                return LoginFailed("Bluesky login was cancelled.");
            }

            try
            {
                using AtProtoAgent agent = oAuthProvider.CreateAgent();

                OAuthLoginState? oAuthLoginState = OAuthLoginState.FromJson(loginState.OAuthState);
                if (oAuthLoginState is null)
                {
                    return LoginFailed("Your Bluesky login expired. Try again.");
                }

                OAuthClient oAuthClient = agent.CreateOAuthClient(oAuthLoginState);

                // Exchanges the code for tokens and validates the issuer, audience and scope.
                bool loggedIn = await agent.ProcessOAuth2LoginResponse(oAuthClient,
                    Request.QueryString.Value ?? string.Empty,
                    cancellationToken);

                if (!loggedIn || !agent.IsAuthenticated || agent.Did is null)
                {
                    return LoginFailed("Bluesky login failed. Try again.");
                }

                string did = agent.Did.ToString();

                // The spec is emphatic that this check is mandatory. The library confirms the token
                // came from the authorization server we expected, but not that the account it
                // authenticated is the one we asked for. Without this a hostile PDS could hand us
                // an authorization for any DID it liked.
                if (!string.Equals(did, loginState.ExpectedDid, StringComparison.Ordinal))
                {
                    logger.LogError("Bluesky returned DID {ReturnedDid} but we requested {ExpectedDid}.",
                        did, loginState.ExpectedDid);
                    await TryRevoke(agent, cancellationToken);
                    return LoginFailed("Bluesky authenticated a different account than the one requested.");
                }

                // Prefer the handle the DID document currently claims over the one that was typed.
                DidDocument? didDocument = await agent.ResolveDidDocument(agent.Did, cancellationToken);
                string handle = BlueskyOAuthProvider.GetHandleFromDidDocument(didDocument) ?? loginState.Handle;

                // Identity is confirmed, so we are finished with Bluesky. Revoke before signing in
                // so a failure here cannot leave the user without a session.
                await TryRevoke(agent, cancellationToken);

                await HttpContext.SignInAsync(BlueskyAuthDefaults.CookieScheme,
                    BuildPrincipal(did, handle, BlueskyAuthDefaults.CookieScheme),
                    new AuthenticationProperties()
                    {
                        IsPersistent = true,
                        IssuedUtc = DateTimeOffset.UtcNow,
                        ExpiresUtc = DateTimeOffset.UtcNow.Add(options.SessionLifetime)
                    });

                logger.LogInformation("Signed in Bluesky user {Handle} ({Did}).", handle, did);

                return Redirect(options.PostLoginRedirect);
            }
            catch (OAuthException ex)
            {
                logger.LogWarning(ex, "Bluesky login callback failed.");
                return LoginFailed("Bluesky login failed. Try again.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure in the Bluesky login callback.");
                return LoginFailed("Bluesky login failed. Try again.");
            }
        }

        /// <summary>
        /// The signed in Bluesky identity, if there is one.
        /// </summary>
        [HttpGet("/api/BlueskyAuth/me")]
        [AllowAnonymous]
        public async Task<IActionResult> Me()
        {
            AuthenticateResult result = await HttpContext.AuthenticateAsync(BlueskyAuthDefaults.CookieScheme);
            if (!result.Succeeded)
            {
                return Ok(new MeResponse(false, null, null));
            }

            return Ok(new MeResponse(true,
                result.Principal.FindFirstValue(BlueskyAuthDefaults.DidClaim),
                result.Principal.FindFirstValue(BlueskyAuthDefaults.HandleClaim)));
        }

        /// <summary>
        /// Issues a bearer token for the signed in identity, for clients that cannot use cookies.
        /// </summary>
        [HttpGet("/api/BlueskyAuth/token")]
        [Authorize(AuthenticationSchemes = BlueskyAuthDefaults.CookieScheme)]
        public IActionResult GetToken()
        {
            string? did = User.FindFirstValue(BlueskyAuthDefaults.DidClaim);
            string? handle = User.FindFirstValue(BlueskyAuthDefaults.HandleClaim);

            if (string.IsNullOrWhiteSpace(did) || string.IsNullOrWhiteSpace(handle))
            {
                return Unauthorized();
            }

            string token = tokenIssuer.Issue(BuildPrincipal(did, handle, BlueskyAuthDefaults.BearerScheme),
                options.SessionLifetime);

            return Ok(new TokenResponse(token,
                (int)options.SessionLifetime.TotalSeconds,
                did,
                handle));
        }

        [HttpPost("/api/BlueskyAuth/logout")]
        [AllowAnonymous]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(BlueskyAuthDefaults.CookieScheme);
            return NoContent();
        }

        private static ClaimsPrincipal BuildPrincipal(string did, string handle, string authenticationScheme)
        {
            Claim[] claims = new[]
            {
                new Claim(BlueskyAuthDefaults.DidClaim, did),
                new Claim(BlueskyAuthDefaults.HandleClaim, handle),
                new Claim(ClaimTypes.NameIdentifier, did),
                new Claim(ClaimTypes.Name, handle)
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationScheme));
        }

        /// <summary>
        /// Best effort revocation. We are done with the session either way, so a failure here is
        /// logged and ignored rather than surfaced to the user.
        /// </summary>
        private async Task TryRevoke(AtProtoAgent agent, CancellationToken cancellationToken)
        {
            try
            {
                await agent.Logout(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to revoke the Bluesky session after verifying identity.");
            }
        }

        private RedirectResult LoginFailed(string message)
        {
            return Redirect($"/?blueskyError={Uri.EscapeDataString(message)}");
        }

        public record LoginRequest(string Handle);

        public record LoginResponse(string AuthUrl);

        public record MeResponse(bool LoggedIn, string? Did, string? Handle);

        public record TokenResponse(string Token, int ExpiresInSeconds, string Did, string Handle);

        public record ErrorResponse(string Message);
    }
}
