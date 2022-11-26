using System.Net;
using System.Text;
using Azure.Security.KeyVault.Secrets;
using LinqToTwitter;
using LinqToTwitter.OAuth;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TwitterController : ControllerBase
    {
        private const string TwitterUsername = "carbikelanesea";

        private readonly ILogger<RedirectController> logger;
        private readonly HttpClient httpClient;
        private readonly string authHeader;
        private readonly string redirectUri;

        public TwitterController(ILogger<RedirectController> logger,
            IWebHostEnvironment environment,
            HttpClient httpClient,
            SecretClient secretClient)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            string unencodedAuthHeader = $"{secretClient.GetSecret("twitter-oauth2-client-id").Value.Value}:" +
                $"{secretClient.GetSecret("twitter-oauth2-client-secret").Value.Value}";
            authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes(unencodedAuthHeader));
            if (environment.IsDevelopment())
            {
                redirectUri = "https://localhost:7152/redirect";
            }
            else
            {
                redirectUri = "https://seattle.carinbikelane.com/redirect";
            }
        }

        [HttpGet("oembed")]
        public async Task<string?> GetOEmbed([FromQuery] string tweetId)
        {
            string url = $"https://twitter.com/{TwitterUsername}/status/{tweetId}";
            HttpClient client = new HttpClient();
            HttpResponseMessage response = await client.GetAsync($"https://publish.twitter.com/oembed?url={url}&dnt=true");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }

        [HttpGet("OAuthUrl")]
        public string GetOAuthUrl()
        {
            return $"https://twitter.com/i/oauth2/authorize?response_type=code&client_id=RXYtYnN5b2hsMUo3ZjlSZ2p6bEE6MTpjaQ&redirect_uri={redirectUri}&scope=tweet.read%20users.read%20offline.access&state=randomstate&code_challenge=plain&code_challenge_method=plain";
        }

        [HttpPost("redirect")]
        public async Task<TwitterOAuthResponse?> ProcessRedirect([FromQuery] string code)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitter.com/2/oauth2/token");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
            FormUrlEncodedContent content = new FormUrlEncodedContent(new Dictionary<string, string>()
            {
                { "grant_type", "authorization_code" },
                { "redirect_uri", redirectUri },
                { "code_verifier", "plain" },
                { "code", code }
            });
            request.Content = content;

            HttpResponseMessage response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string failedResponseString = await response.Content.ReadAsStringAsync();
                logger.LogError("Failed to get Twitter access token");
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return null;
            }

            JObject responseObject = JObject.Parse(await response.Content.ReadAsStringAsync());
            return ParseResponse(responseObject);
        }

        [HttpPost("RefreshToken")]
        public async Task<TwitterOAuthResponse?> RefreshAccessToken([FromBody] TwitterRefreshTokenRequest tokenRequest)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitter.com/2/oauth2/token");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
            FormUrlEncodedContent content = new FormUrlEncodedContent(new Dictionary<string, string>()
            {
                { "refresh_token", tokenRequest.RefreshToken },
                { "grant_type", "refresh_token" }
            });
            request.Content = content;

            HttpResponseMessage response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string failedResponseString = await response.Content.ReadAsStringAsync();
                logger.LogError("Failed to get refreshed Twitter access token");
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return null;
            }

            JObject responseObject = JObject.Parse(await response.Content.ReadAsStringAsync());
            return ParseResponse(responseObject);
        }

        [HttpPost("GetTwitterUsername")]
        public async Task<TwitterUser?> GetTwitterUsername([FromBody] GetTwitterUsernameRequest usernameRequest)
        {
            OAuth2Authorizer userAuth = new OAuth2Authorizer()
            {
                CredentialStore = new OAuth2CredentialStore()
                {
                    AccessToken = usernameRequest.AccessToken
                }
            };
            TwitterContext twitterContext = new TwitterContext(userAuth);
            try
            {
                TwitterUserQuery? response = await (from user in twitterContext.TwitterUser
                                                    where user.Type == UserType.Me
                                                    select user).SingleOrDefaultAsync();
                TwitterUser? twitterUser = response?.Users?.SingleOrDefault();
                if (twitterUser != null)
                {
                    return twitterUser;
                }
                else
                {
                    Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get Twitter username.");
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return null;
            }
        }

        private TwitterOAuthResponse ParseResponse(JObject responseObject)
        {
            return new TwitterOAuthResponse((string)responseObject["access_token"]!,
                (string)responseObject["refresh_token"]!,
                (int)responseObject["expires_in"]!);
        }

        public class TwitterOAuthResponse
        {
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public DateTime ExpiresAt { get; set; }

            public TwitterOAuthResponse(string accessToken,
                string refreshToken,
                int expiresIn)
            {
                AccessToken = accessToken;
                RefreshToken = refreshToken;
                ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
            }
        }

        public class TwitterRefreshTokenRequest
        {
            public string RefreshToken { get; set; } = string.Empty;
        }

        public class GetTwitterUsernameRequest
        {
            public string AccessToken { get; set; } = string.Empty;
        }
    }
}
