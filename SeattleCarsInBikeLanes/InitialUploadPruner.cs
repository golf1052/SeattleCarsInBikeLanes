using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SeattleCarsInBikeLanes.Controllers;

namespace SeattleCarsInBikeLanes
{
    public class InitialUploadPruner
    {
        private readonly ILogger<InitialUploadPruner> logger;
        private readonly BlobContainerClient blobContainerClient;
        private readonly TimeSpan aliveDuration;
        private readonly string blobPrefix;
        private Task deleteTask;

        public InitialUploadPruner(ILogger<InitialUploadPruner> logger,
            BlobContainerClient blobContainerClient,
            TimeSpan aliveDuration,
            string blobPrefix = UploadController.InitialUploadPrefix)
        {
            this.logger = logger;
            this.blobContainerClient = blobContainerClient;
            this.aliveDuration = aliveDuration;
            this.blobPrefix = blobPrefix;
            deleteTask = CheckForStaleUploads();
            _ = CheckCheckDeleteTask();
        }

        private async Task CheckForStaleUploads()
        {
            while (true)
            {
                var blobs = blobContainerClient.GetBlobsAsync(
                    BlobTraits.None,
                    BlobStates.None,
                    blobPrefix,
                    CancellationToken.None);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                await foreach (var blob in blobs)
                {
                    if (blob.Properties.CreatedOn == null)
                    {
                        logger.LogWarning($"Blob doesn't have created on date: {blob.Name}");
                        continue;
                    }
                    else
                    {
                        if (blob.Properties.CreatedOn.Value.Add(aliveDuration) < now)
                        {
                            BlobClient blobClient = blobContainerClient.GetBlobClient(blob.Name);
                            logger.LogInformation($"Pruner deleting {blob.Name} as it was created on {blob.Properties.CreatedOn.Value}");
                            await blobClient.DeleteAsync();
                        }
                    }
                }
                await Task.Delay(aliveDuration);
            }
        }

        private async Task CheckCheckDeleteTask()
        {
            while (true)
            {
                if (deleteTask.IsFaulted)
                {
                    logger.LogError("Check for stale uploads task has failed, restarting.");
                    if (deleteTask.Exception != null)
                    {
                        foreach (var exception in deleteTask.Exception.InnerExceptions)
                        {
                            logger.LogError(exception, "InnerException");
                        }
                    }
                    deleteTask = CheckForStaleUploads();
                }
                await Task.Delay((int)aliveDuration.TotalMilliseconds / 2);
            }
        }
    }
}
