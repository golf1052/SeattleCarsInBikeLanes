using Microsoft.Azure.Cosmos.Spatial;
using Newtonsoft.Json;

namespace SeattleCarsInBikeLanes.Database.Models
{
    public class ReportedItem
    {
        [JsonProperty("id")]
        public string TweetId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int NumberOfCars { get; set; }
        public DateOnly? Date { get; set; }
        public TimeOnly? Time { get; set; }
        public string LocationString { get; set; } = string.Empty;
        public Point? Location { get; set; }
        public List<string> ImageUrls { get; set; } = new List<string>();
        public List<string> ImgurUrls { get; set; } = new List<string>();
        public bool Latest { get; set; } = false;
    }
}
