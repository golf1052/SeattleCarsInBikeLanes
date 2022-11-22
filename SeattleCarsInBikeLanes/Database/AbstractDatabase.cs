using Microsoft.Azure.Cosmos;
using SeattleCarsInBikeLanes.Database.Models;

namespace SeattleCarsInBikeLanes.Database
{
    public abstract class AbstractDatabase<T>
    {
        protected readonly ILogger<AbstractDatabase<T>> logger;
        protected readonly Container container;

        public AbstractDatabase(ILogger<AbstractDatabase<T>> logger, Container container)
        {
            this.logger = logger;
            this.container = container;
        }

        public async Task<bool> AddItem(T item, string id)
        {
            try
            {
                await container.CreateItemAsync(item, new PartitionKey(id));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to add item to database. {id}");
                return false;
            }
        }

        public async Task<bool> UpdateItem(T item, string id)
        {
            try
            {
                await container.ReplaceItemAsync(item, id, new PartitionKey(id));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to update item in database. {id}");
                return false;
            }
        }

        public async Task<T?> GetItem(string id)
        {
            try
            {
                T? item = await container.ReadItemAsync<T>(id, new PartitionKey(id));
                return item;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to get item from database. {id}");
                return default;
            }
        }

        public async Task<bool> DeleteItem(string id)
        {
            try
            {
                await container.DeleteItemAsync<T>(id, new PartitionKey(id));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to delete item from database. {id}");
                return false;
            }
        }

        protected async Task<List<ReportedItem>?> RunQuery(string? query = null)
        {
            using FeedIterator<ReportedItem> iterator = container.GetItemQueryIterator<ReportedItem>(query);
            return await ProcessIterator(iterator);
        }

        protected async Task<List<ReportedItem>?> RunQuery(QueryDefinition query)
        {
            using FeedIterator<ReportedItem> iterator = container.GetItemQueryIterator<ReportedItem>(query);
            return await ProcessIterator(iterator);
        }

        protected async Task<List<ReportedItem>?> ProcessIterator(FeedIterator<ReportedItem> iterator)
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
