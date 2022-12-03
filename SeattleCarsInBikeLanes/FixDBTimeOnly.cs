using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Spatial;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using SeattleCarsInBikeLanes.Database;

namespace SeattleCarsInBikeLanes
{
    public class FixDBTimeOnly
    {
        private readonly BadReportedItemsDatabase badReportedItemsDatabase;

        public FixDBTimeOnly(Microsoft.Azure.Cosmos.Database database)
        {
            Container container = database.GetContainer("items");
            badReportedItemsDatabase = new BadReportedItemsDatabase(NullLogger<AbstractDatabase<BadReportedItem>>.Instance, container);
            _ = Run();
        }

        public async Task Run()
        {
            List<BadReportedItem>? badItems = await badReportedItemsDatabase.GetAllItems();
            if (badItems == null)
            {
                throw new Exception();
            }

            foreach (var badItem in badItems)
            {
                if (!string.IsNullOrWhiteSpace(badItem.Time))
                {
                    TimeOnly time = TimeOnly.Parse(badItem.Time);
                    badItem.Time = time.ToString("HH:mm:ss");
                    await badReportedItemsDatabase.UpdateItem(badItem, badItem.TweetId);
                }
            }

            System.Diagnostics.Debug.WriteLine("done");
        }

        public class BadReportedItemsDatabase : AbstractDatabase<BadReportedItem>
        {
            public BadReportedItemsDatabase(ILogger<AbstractDatabase<BadReportedItem>> logger, Container container) : base(logger, container)
            {
            }

            public async Task<List<BadReportedItem>?> GetAllItems()
            {
                return await RunQuery();
            }
        }

        public class BadReportedItem
        {
            [JsonProperty("id")]
            public string TweetId { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public int NumberOfCars { get; set; }
            public DateOnly? Date { get; set; }
            public string? Time { get; set; }
            public string LocationString { get; set; } = string.Empty;
            public Point? Location { get; set; }
            public List<string> ImageUrls { get; set; } = new List<string>();
            public List<string> ImgurUrls { get; set; } = new List<string>();
            public string? TwitterLink { get; set; }
            public string? MastodonLink { get; set; }
            public bool Latest { get; set; } = false;
        }
    }
}
