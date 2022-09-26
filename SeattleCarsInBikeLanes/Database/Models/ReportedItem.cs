using Newtonsoft.Json;

namespace SeattleCarsInBikeLanes.Database.Models
{
    public class ReportedItem
    {
        [JsonProperty("id")]
        public string TweetId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int NumberOfCars { get; set; }
        public string? Date { get; set; }
        public string? Time { get; set; }
        public string LocationString { get; set; } = string.Empty;
        public LatLon? Location { get; set; }
        public List<string> ImageUrls { get; set; } = new List<string>();
        public bool Latest { get; set; } = false;
    }

    public struct LatLon
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
