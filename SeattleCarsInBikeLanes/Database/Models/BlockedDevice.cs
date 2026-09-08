using Newtonsoft.Json;

namespace SeattleCarsInBikeLanes.Database.Models
{
    public class BlockedDevice
    {
        [JsonProperty("id")]
        public string DeviceId { get; set; } = string.Empty;

        [JsonProperty("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
