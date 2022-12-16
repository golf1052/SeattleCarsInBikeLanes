using Azure.Security.KeyVault.Secrets;

namespace SeattleCarsInBikeLanes.Providers
{
    public class SlackbotProvider
    {
        private readonly ILogger<SlackbotProvider> logger;
        private readonly HttpClient httpClient;
        private readonly string botToken;
        private readonly string userId;

        public SlackbotProvider(ILogger<SlackbotProvider> logger,
            HttpClient httpClient,
            SecretClient secretClient)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            botToken = secretClient.GetSecret("slackbot-token").Value.Value;
            userId = secretClient.GetSecret("slack-user-id").Value.Value;
        }

        public async Task SendSlackMessage(string message)
        {
            try
            {
                Dictionary<string, string> keys = new Dictionary<string, string>()
                {
                    { "token", botToken },
                    { "channel", userId },
                    { "text", message },
                    { "username", "Seattle Cars in Bike Lanes" },
                    { "icon_url", "https://seattle.carinbikelane.com/favicon.png" }
                };
                FormUrlEncodedContent content = new FormUrlEncodedContent(keys);
                await httpClient.PostAsync("https://slack.com/api/chat.postMessage", content);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to send message to Slack. Message: {message}");
            }
        }
    }
}
