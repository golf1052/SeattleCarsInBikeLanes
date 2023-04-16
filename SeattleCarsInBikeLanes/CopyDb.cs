using Azure.Identity;
using Microsoft.Azure.Cosmos;
using SeattleCarsInBikeLanes.Database.Models;

namespace SeattleCarsInBikeLanes
{
    public class CopyDb
    {
        private readonly CosmosClient sourceCosmosClient;
        private readonly CosmosClient destinationCosmosClient;

        public CopyDb(DefaultAzureCredential credentials)
        {
            sourceCosmosClient = new CosmosClient("https://seattle-carsinbikelanes-db.documents.azure.com:443/", credentials);
            destinationCosmosClient = new CosmosClient("https://seattle-carsinbikelanes-db.documents.azure.com:443/", credentials);
            // _ = Run();
        }

        private async Task Run()
        {
            System.Diagnostics.Debug.WriteLine("Starting DB copy");
            Microsoft.Azure.Cosmos.Database sourceDatabase = sourceCosmosClient.GetDatabase("seattle");
            Microsoft.Azure.Cosmos.Database destinationDatabase = destinationCosmosClient.GetDatabase("seattle");
            Container sourceContainer = sourceDatabase.GetContainer("items2");
            Container destinationContainer = destinationDatabase.GetContainer("items");
            using FeedIterator<ReportedItem> iterator = sourceContainer.GetItemQueryIterator<ReportedItem>();
            List<ReportedItem> items = new List<ReportedItem>();
            while (iterator.HasMoreResults)
            {
                FeedResponse<ReportedItem> currentResults = await iterator.ReadNextAsync();
                items.AddRange(currentResults);
            }
            
            foreach (ReportedItem item in items)
            {
                await destinationContainer.CreateItemAsync(item, new PartitionKey(item.TweetId));
            }
            System.Diagnostics.Debug.WriteLine("Finished DB copy");
        }
    }
}
