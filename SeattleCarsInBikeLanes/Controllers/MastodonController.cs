using golf1052.Mastodon;
using golf1052.Mastodon.Models.Accounts;
using golf1052.Mastodon.Models.Apps.OAuth;
using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MastodonController : ControllerBase
    {
        private const string RedirectUrl = "https://seattle.carinbikelane.com/mastodonredirect";
        private readonly List<string> Scopes = new List<string>() { "read:accounts" };
        private readonly MastodonClientProvider mastodonClientProvider;

        public MastodonController(MastodonClientProvider mastodonClientProvider)
        {
            this.mastodonClientProvider = mastodonClientProvider;
        }

        [HttpPost("GetOAuthUrl")]
        public async Task<MastodonOAuthUrlResponse> GetOAuthUrl(MastodonOAuthUrlRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ServerUrl))
            {
                throw new ArgumentNullException(nameof(request.ServerUrl));
            }

            Uri endpointUri;
            try
            {
                endpointUri = new Uri(request.ServerUrl);
            }
            catch (UriFormatException)
            {
                throw new ArgumentException($"Failed to find Mastodon instance {request.ServerUrl}");
            }

            MastodonClient mastodonClient = await mastodonClientProvider.GetClient(endpointUri);
            return new MastodonOAuthUrlResponse()
            {
                AuthUrl = mastodonClient.AuthorizeUser(RedirectUrl, Scopes)
            };
        }

        [HttpPost("GetMastodonUsername")]
        public async Task<MastodonUsernameResponse> GetMastodonUsername([FromBody] MastodonUsernameRequest request)
        {
            Uri endpoint = new Uri(request.ServerUrl);
            MastodonClient mastodonClient = await mastodonClientProvider.GetUserClient(endpoint, request.AccessToken);
            MastodonAccount mastodonAccount = await mastodonClient.VerifyCredentials();
            return new MastodonUsernameResponse()
            {
                Username = mastodonAccount.Username,
                FullUsername = $"@{mastodonAccount.Username}@{endpoint.Host}"
            };
        }

        [HttpPost("redirect")]
        public async Task<MastodonToken> ProcessRedirect([FromQuery] string code, [FromBody] MastodonOAuthUrlRequest request)
        {
            MastodonClient mastodonClient = await mastodonClientProvider.GetClient(new Uri(request.ServerUrl));
            return await mastodonClient.ObtainToken("authorization_code", RedirectUrl, code, Scopes);
        }

        public class MastodonOAuthUrlRequest
        {
            public string ServerUrl { get; set; } = string.Empty;
        }

        public class MastodonOAuthUrlResponse
        {
            public string AuthUrl { get; set; } = string.Empty;
        }

        public class MastodonUsernameRequest
        {
            public string AccessToken { get; set; } = string.Empty;
            public string ServerUrl { get; set; } = string.Empty;
        }

        public class MastodonUsernameResponse
        {
            public string Username { get; set; } = string.Empty;
            public string FullUsername { get; set; } = string.Empty;
        }
    }
}
