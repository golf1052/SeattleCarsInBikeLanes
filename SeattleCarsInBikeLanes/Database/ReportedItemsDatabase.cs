using System.Text;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;

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
            List<ReportedItem>? items = await RunQuery("select * from items where items.Latest = true");
            if (items == null || items.Count == 0)
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

        public async Task<List<ReportedItem>?> SearchItems(ReportedItemsSearchRequest request)
        {
            IQueryable<ReportedItem> query = itemsContainer.GetItemLinqQueryable<ReportedItem>();
            if (request.MinCars != null)
            {
                query = query.Where(i => i.NumberOfCars >= request.MinCars.Value);
            }

            if (request.MaxCars != null)
            {
                query = query.Where(i => i.NumberOfCars <= request.MaxCars.Value);
            }

            if (request.MinDate != null || request.MaxDate != null)
            {
                query = query.Where(i => i.Date != null);

                if (request.MinDate != null)
                {
                    query = query.Where(i => i.Date!.Value >= request.MinDate.Value);
                }

                if (request.MaxDate != null)
                {
                    query = query.Where(i => i.Date!.Value <= request.MaxDate.Value);
                }
            }

            if (request.MinTime != null || request.MaxTime != null)
            {
                query = query.Where(i => i.Time != null);

                if (request.MinTime != null)
                {
                    query = query.Where(i => i.Time!.Value >= request.MinTime.Value);
                }

                if (request.MaxTime != null)
                {
                    query = query.Where(i => i.Time!.Value <= request.MaxTime.Value);
                }
            }

            if (request.Location != null && request.DistanceFromLocationInMiles != null)
            {
                query = query.Where(i => i.Location != null)
                    .Where(i => i.Location!.Distance(new Point(request.Location)) <=
                    request.DistanceFromLocationInMiles * 1609.344);
            }

            using FeedIterator<ReportedItem> iterator = query.ToFeedIterator();
            return await ProcessIterator(iterator);
        }

        public async Task<List<ReportedItem>?> GetAllItems()
        {
            return await RunQuery();
        }

        public async Task<ReportedItem?> GetItem(string tweetId)
        {
            var items = await GetItems(new List<string>() { tweetId });
            if (items == null || items.Count == 0)
            {
                return null;
            }
            return items[0];
        }

        public async Task<List<ReportedItem>?> GetItems(List<string> tweetIds)
        {
            if (tweetIds.Count == 0)
            {
                return new List<ReportedItem>();
            }

            StringBuilder queryStringBuilder = new StringBuilder("select * from items where ");
            List<string> queryParts = new List<string>();
            for (int i = 0; i < tweetIds.Count; i++)
            {
                queryParts.Add($"startswith(items.id, @t{i})");
            }
            queryStringBuilder.AppendJoin(" or ", queryParts);

            QueryDefinition query = new QueryDefinition(queryStringBuilder.ToString());
            for (int i = 0; i < tweetIds.Count; i++)
            {
                string id = tweetIds[i];
                query.WithParameter($"@t{i}", id);
            }

            return await RunQuery(query);
        }

        public async Task<bool> DeleteItem(ReportedItem item)
        {
            try
            {
                await itemsContainer.DeleteItemAsync<ReportedItem>(item.TweetId, new PartitionKey(item.TweetId));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to delete item: {item.TweetId}");
                return false;
            }
        }

        public async Task<bool> DeleteAllItems()
        {
            List<ReportedItem>? allItems = await GetAllItems();
            if (allItems == null)
            {
                return false;
            }

            bool allDeleted = true;
            foreach (var item in allItems)
            {
                allDeleted &= await DeleteItem(item);
            }
            return allDeleted;
        }

        private async Task<List<ReportedItem>?> RunQuery(string? query = null)
        {
            using FeedIterator<ReportedItem> iterator = itemsContainer.GetItemQueryIterator<ReportedItem>(query);
            return await ProcessIterator(iterator);
        }

        private async Task<List<ReportedItem>?> RunQuery(QueryDefinition query)
        {
            using FeedIterator<ReportedItem> iterator = itemsContainer.GetItemQueryIterator<ReportedItem>(query);
            return await ProcessIterator(iterator);
        }

        private async Task<List<ReportedItem>?> ProcessIterator(FeedIterator<ReportedItem> iterator)
        {
            try
            {
                List<ReportedItem> items = new List<ReportedItem>();
                while (iterator.HasMoreResults)
                {
                    FeedResponse<ReportedItem> currentResults = await iterator.ReadNextAsync();
                    items.AddRange(currentResults);
                }
                return items;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get results from iterator.");
                return null;
            }
        }
    }
}
