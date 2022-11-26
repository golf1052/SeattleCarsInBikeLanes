using Azure.Security.KeyVault.Secrets;
using golf1052.Mastodon;
using golf1052.Mastodon.Models.Apps;
using golf1052.Mastodon.Models.Apps.OAuth;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;

namespace SeattleCarsInBikeLanes.Providers
{
    public class MastodonClientProvider
    {
        private const string ClientName = "SeattleCarInBikeLaneWebsite";
        private const string Website = "https://seattle.carinbikelane.com";

        private readonly ILogger<MastodonClientProvider> logger;
        private readonly MastodonOAuthMappingDatabase mastodonOAuthMappingDatabase;
        private readonly SecretClient secretClient;
        private readonly ILogger<MastodonClient> clientLogger;
        private readonly HttpClient httpClient;

        private readonly string redirectUri;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string accessToken;

        public MastodonClientProvider(ILogger<MastodonClientProvider> logger,
            IWebHostEnvironment environment,
            MastodonOAuthMappingDatabase mastodonOAuthMappingDatabase,
            SecretClient secretClient,
            ILogger<MastodonClient> clientLogger,
            HttpClient httpClient)
        {
            this.logger = logger;
            this.mastodonOAuthMappingDatabase = mastodonOAuthMappingDatabase;
            this.secretClient = secretClient;
            this.clientLogger = clientLogger;
            this.httpClient = httpClient;

            if (environment.IsDevelopment())
            {
                redirectUri = "https://localhost:7152/mastodonredirect";
                clientId = "-ClientId-Dev";
                clientSecret = "-ClientSecret-Dev";
                accessToken = "-AccessToken-Dev";
            }
            else
            {
                redirectUri = "https://seattle.carinbikelane.com/mastodonredirect";
                clientId = "-ClientId";
                clientSecret = "-ClientSecret";
                accessToken = "-AccessToken";
            }
        }

        public async Task<MastodonClient> GetClient(Uri endpointUri)
        {
            MastodonOAuthMapping? mapping = await mastodonOAuthMappingDatabase.GetItem(endpointUri.IdnHost);
            if (mapping == null)
            {
                logger.LogInformation($"{endpointUri} does not exist in DB. Registering new application.");
                return await CreateClient(endpointUri);
            }

            MastodonClient mastodonClient = new MastodonClient(endpointUri, httpClient, clientLogger);
            mastodonClient.ClientId = secretClient.GetSecret($"{mapping.SecretPrefix}{clientId}").Value.Value;
            mastodonClient.ClientSecret = secretClient.GetSecret($"{mapping.SecretPrefix}{clientSecret}").Value.Value;
            mastodonClient.AccessToken = secretClient.GetSecret($"{mapping.SecretPrefix}{accessToken}").Value.Value;
            return mastodonClient;
        }

        public async Task<MastodonClient> GetUserClient(Uri endpointUri, string accessToken)
        {
            MastodonOAuthMapping? mapping = await mastodonOAuthMappingDatabase.GetItem(endpointUri.IdnHost);
            if (mapping == null)
            {
                string errorMessage = $"{endpointUri} does not exist in DB. Not creating a new application because user is requesting.";
                logger.LogError(errorMessage);
                throw new Exception(errorMessage);
            }

            MastodonClient mastodonClient = new MastodonClient(endpointUri, httpClient, clientLogger);
            mastodonClient.ClientId = secretClient.GetSecret($"{mapping.SecretPrefix}{clientId}").Value.Value;
            mastodonClient.ClientSecret = secretClient.GetSecret($"{mapping.SecretPrefix}{clientSecret}").Value.Value;
            mastodonClient.AccessToken = accessToken;
            return mastodonClient;
        }

        public MastodonClient GetServerClient()
        {
            MastodonClient mastodonClient = new MastodonClient(new Uri("https://social.ridetrans.it"), httpClient, clientLogger);
            mastodonClient.AccessToken = secretClient.GetSecret("social-ridetransit-access-token").Value.Value;
            return mastodonClient;
        }

        private async Task<MastodonClient> CreateClient(Uri endpointUri)
        {
            try
            {
                HttpResponseMessage endpointTestResponse = await httpClient.GetAsync(endpointUri);
                if (!endpointTestResponse.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to connect to endpoint, received status code {endpointTestResponse.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to connect to endpoint.", ex);
            }
            
            MastodonClient mastodonClient = new MastodonClient(endpointUri, httpClient, clientLogger);
            MastodonApplication app = await mastodonClient.CreateApplication(ClientName,
                redirectUri,
                new List<string>() { "read:accounts", "crypto" },
                Website);
            
            if (string.IsNullOrWhiteSpace(app.ClientId))
            {
                throw new Exception("New client id from Mastodon is null.");
            }

            if (string.IsNullOrWhiteSpace(app.ClientSecret))
            {
                throw new Exception("New client secret from Mastodon is null.");
            }

            mastodonClient.ClientId = app.ClientId;
            mastodonClient.ClientSecret = app.ClientSecret;

            MastodonToken clientToken = await mastodonClient.ObtainToken("client_credentials",
                "urn:ietf:wg:oauth:2.0:oob",
                null,
                new List<string>() { "read:accounts", "crypto" });

            if (string.IsNullOrWhiteSpace(clientToken.AccessToken))
            {
                throw new Exception("New access token from Mastodon is null.");
            }

            mastodonClient.AccessToken = clientToken.AccessToken;
            string prefix = Guid.NewGuid().ToString();
            MastodonOAuthMapping mapping = new MastodonOAuthMapping()
            {
                Host = endpointUri.IdnHost,
                SecretPrefix = prefix
            };
            bool createdMapping = await mastodonOAuthMappingDatabase.AddItem(mapping, mapping.Host);
            if (!createdMapping)
            {
                throw new Exception($"Failed to create OAuth mapping in database for {endpointUri}");
            }

            secretClient.SetSecret($"{prefix}{clientId}", app.ClientId);
            secretClient.SetSecret($"{prefix}{clientSecret}", app.ClientSecret);
            secretClient.SetSecret($"{prefix}{accessToken}", clientToken.AccessToken);
            return mastodonClient;
        }
    }
}
