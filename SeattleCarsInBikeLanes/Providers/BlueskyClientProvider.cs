using Azure.Security.KeyVault.Secrets;
using golf1052.atproto.net;
using golf1052.atproto.net.Models.AtProto.Server;

namespace SeattleCarsInBikeLanes.Providers
{
    public class BlueskyClientProvider
    {
        private readonly ILogger<BlueskyClientProvider> logger;
        private readonly SecretClient secretClient;
        private readonly HttpClient httpClient;
        private readonly AtProtoClient blueskyClient;

        public BlueskyClientProvider(ILogger<BlueskyClientProvider> logger,
            SecretClient secretClient,
            HttpClient httpClient)
        {
            this.logger = logger;
            this.secretClient = secretClient;
            this.httpClient = httpClient;
            blueskyClient = new AtProtoClient(httpClient);
        }

        public virtual async Task<AtProtoClient> GetClient()
        {
            string password = secretClient.GetSecret("bluesky-app-password").Value.Value;
            await blueskyClient.CreateSession(new CreateSessionRequest()
            {
                Identifier = "seattle.carinbikelane.com",
                Password = password
            });
            return blueskyClient;
        }
    }
}
