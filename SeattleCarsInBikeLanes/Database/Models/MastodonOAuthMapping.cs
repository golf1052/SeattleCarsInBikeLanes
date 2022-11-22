using Newtonsoft.Json;

namespace SeattleCarsInBikeLanes.Database.Models
{
    public class MastodonOAuthMapping
    {
        /// <summary>
        /// Uri.IdnHost
        /// </summary>
        [JsonProperty("id")]
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// Guid
        /// </summary>
        public string SecretPrefix { get; set; } = string.Empty;
    }
}
