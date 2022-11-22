﻿using Azure.Security.KeyVault.Secrets;
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
        private const string ClientId = "-ClientId";
        private const string ClientSecret = "-ClientSecret";
        private const string AccessToken = "-AccessToken";

        private readonly ILogger<MastodonClientProvider> logger;
        private readonly MastodonOAuthMappingDatabase mastodonOAuthMappingDatabase;
        private readonly SecretClient secretClient;
        private readonly ILogger<MastodonClient> clientLogger;
        private readonly HttpClient httpClient;

        public MastodonClientProvider(ILogger<MastodonClientProvider> logger,
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
            mastodonClient.ClientId = secretClient.GetSecret($"{mapping.SecretPrefix}{ClientId}").Value.Value;
            mastodonClient.ClientSecret = secretClient.GetSecret($"{mapping.SecretPrefix}{ClientSecret}").Value.Value;
            mastodonClient.AccessToken = secretClient.GetSecret($"{mapping.SecretPrefix}{AccessToken}").Value.Value;
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
                new List<string>()
                {
                    "urn:ietf:wg:oauth:2.0:oob",
                    "https://seattle.carinbikelane.com/mastodonredirect"
                },
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

            secretClient.SetSecret($"{prefix}{ClientId}", app.ClientId);
            secretClient.SetSecret($"{prefix}{ClientSecret}", app.ClientSecret);
            secretClient.SetSecret($"{prefix}{AccessToken}", clientToken.AccessToken);
            return mastodonClient;
        }
    }
}
