using System.Text;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Database
{
    public class ReportedItemsDatabase : AbstractDatabase<ReportedItem>
    {
        public ReportedItemsDatabase(ILogger<ReportedItemsDatabase> logger, Container itemsContainer) :
            base(logger, itemsContainer)
        {
        }

        public virtual async Task<bool> AddReportedItem(ReportedItem item)
        {
            return await base.AddItem(item, item.TweetId);
        }

        public async Task<bool> UpdateReportedItem(ReportedItem item)
        {
            return await base.UpdateItem(item, item.TweetId);
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
            IQueryable<ReportedItem> query = container.GetItemLinqQueryable<ReportedItem>();
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

        public new async Task<ReportedItem?> GetItem(string tweetId)
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

        public async Task<List<ReportedItem>?> GetItemUsingIdentifier(string identifier)
        {
            IQueryable<ReportedItem> query = container.GetItemLinqQueryable<ReportedItem>()
                .Where(i => i.TweetId.Contains(identifier) ||
                    (i.TwitterLink != null && i.TwitterLink.Contains(identifier)) ||
                    (i.MastodonLink != null && i.MastodonLink.Contains(identifier)) ||
                    (i.BlueskyLink != null && i.BlueskyLink.Contains(identifier)) ||
                    (i.ThreadsLink != null && i.ThreadsLink.Contains(identifier)));

            using FeedIterator<ReportedItem> iterator = query.ToFeedIterator();
            return await ProcessIterator(iterator);
        }

        public async Task<List<ReportedItem>?> GetLatestItems(int? count)
        {
            if (count == null)
            {
                return await GetAllItems();
            }

            IQueryable<ReportedItem> query = container.GetItemLinqQueryable<ReportedItem>()
                .OrderByDescending(i => i.CreatedAt).Take(count.Value);

            using FeedIterator<ReportedItem> iterator = query.ToFeedIterator();
            return await ProcessIterator(iterator);
        }

        public async Task<bool> DeleteItem(ReportedItem item)
        {
            return await base.DeleteItem(item.TweetId);
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

        /// <summary>
        /// Get a list of reported items in the given date range (inclusive) with the most cars.
        /// </summary>
        /// <param name="startDate">Start date</param>
        /// <param name="endDate">End date (inclusive)</param>
        /// <returns>List of reported items in the given date range</returns>
        public async Task<List<ReportedItem>> GetMostCars(DateOnly startDate, DateOnly endDate)
        {
            IQueryable<ReportedItem> query = container.GetItemLinqQueryable<ReportedItem>()
                .Where(i => i.Date >= startDate && i.Date <= endDate)
                .OrderByDescending(i => i.NumberOfCars);

            using FeedIterator<ReportedItem> iterator = query.ToFeedIterator();
            List<ReportedItem>? items = await ProcessIterator(iterator);
            if (items == null || items.Count == 0)
            {
                throw new Exception("Found no reported items");
            }

            ReportedItem firstItem = items[0];
            int range = 1;
            for (int i = 1; i < items.Count; i++)
            {
                ReportedItem item = items[i];
                if (item.NumberOfCars == firstItem.NumberOfCars)
                {
                    range = i + 1;
                }
            }

            return items.GetRange(0, range);
        }
    }
}
