using LinqToTwitter;
using LinqToTwitter.Common;
using Microsoft.Azure.Cosmos;
using SeattleCarsInBikeLanes.Database.Models;

namespace SeattleCarsInBikeLanes.Database
{
    public class ReportedItemsDatabase
    {
        private ILogger<ReportedItemsDatabase> logger;
        private readonly Container itemsContainer;

        public ReportedItemsDatabase(ILogger<ReportedItemsDatabase> logger, Container itemsContainer)
        {
            this.logger = logger;
            this.itemsContainer = itemsContainer;
        }

        public async Task<bool> AddReportedItem(ReportedItem item)
        {
            try
            {
                await itemsContainer.CreateItemAsync(item, new PartitionKey(item.TweetId));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to add reported item to database.");
                return false;
            }
        }

        public async Task<bool> UpdateReportedItem(ReportedItem item)
        {
            try
            {
                await itemsContainer.ReplaceItemAsync(item, item.TweetId, new PartitionKey(item.TweetId));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update reported item.");
                return false;
            }
        }

        public async Task<ReportedItem?> GetLatestReportedItem()
        {
            try
            {
                List<ReportedItem> items = new List<ReportedItem>();
                using FeedIterator<ReportedItem> itemsIterator = itemsContainer.GetItemQueryIterator<ReportedItem>(
                    "select * from items where items.Latest = true");
                while (itemsIterator.HasMoreResults)
                {
                    FeedResponse<ReportedItem> currentResults = await itemsIterator.ReadNextAsync();
                    foreach (ReportedItem item in currentResults)
                    {
                        items.Add(item);
                    }
                }

                if (items.Count == 0)
                {
                    return null;
                }

                if (items.Count > 1)
                {
                    string markedItems = string.Join(' ', items.Select(i => i.TweetId));
                    logger.LogWarning($"More than 1 item marked as latest. {markedItems}");
                    return null;
                }

                return items[0];
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get latest item.");
                return null;
            }
        }

        public async Task<List<ReportedItem>?> GetAllItems()
        {
            try
            {
                List<ReportedItem> items = new List<ReportedItem>();
                using FeedIterator<ReportedItem> itemsIterator = itemsContainer.GetItemQueryIterator<ReportedItem>();
                while (itemsIterator.HasMoreResults)
                {
                    FeedResponse<ReportedItem> currentResults = await itemsIterator.ReadNextAsync();
                    foreach (ReportedItem item in currentResults)
                    {
                        items.Add(item);
                    }
                }
                return items;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get all items.");
                return null;
            }
        }
    }
}
