using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;

namespace SeattleCarsInBikeLanes.Providers
{
    /// <summary>
    /// The set of devices that are not allowed to submit reports.
    /// </summary>
    /// <remarks>
    /// The mobile app sends a device identifier with every upload so that abuse can be dealt with
    /// by blocking one device rather than by taking uploads away from everyone. The list lives in
    /// the same blob container as the uploads so it can be edited without a deployment.
    /// </remarks>
    public class DeviceBlocklistProvider
    {
        /// <summary>
        /// Where the list lives, relative to the uploads container.
        /// </summary>
        public const string BlobName = "blockeddevices.json";

        private const string CacheKey = "DeviceBlocklist";

        /// <summary>
        /// How long a fetched list is trusted for.
        /// </summary>
        /// <remarks>
        /// Short enough that blocking somebody takes effect while the abuse is still happening, but
        /// long enough that a burst of uploads does not turn into a burst of blob reads.
        /// </remarks>
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private readonly ILogger<DeviceBlocklistProvider> logger;
        private readonly BlobContainerClient blobContainerClient;
        private readonly IMemoryCache memoryCache;

        public DeviceBlocklistProvider(ILogger<DeviceBlocklistProvider> logger,
            BlobContainerClient blobContainerClient,
            IMemoryCache memoryCache)
        {
            this.logger = logger;
            this.blobContainerClient = blobContainerClient;
            this.memoryCache = memoryCache;
        }

        public async Task<bool> IsBlocked(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                // Uploads from the website have no device id, and blocking those would break the
                // site for everyone.
                return false;
            }

            HashSet<string> blocked = await GetBlockedDevices();
            return blocked.Contains(deviceId);
        }

        private async Task<HashSet<string>> GetBlockedDevices()
        {
            if (memoryCache.TryGetValue(CacheKey, out HashSet<string>? cached) && cached != null)
            {
                return cached;
            }

            HashSet<string> blocked = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                BlobClient blobClient = blobContainerClient.GetBlobClient(BlobName);

                // Downloading straight away, and treating a 404 as an empty list, avoids both the
                // extra round trip of an existence check and the race between the two calls.
                BlobDownloadResult download = await blobClient.DownloadContentAsync();
                List<string>? deviceIds = download.Content.ToObjectFromJson<List<string>>();
                if (deviceIds != null)
                {
                    blocked = deviceIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToHashSet(StringComparer.Ordinal);
                }
            }
            catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
            {
                // No list has been uploaded, so nothing is blocked.
            }
            catch (Exception ex) when (ex is RequestFailedException or JsonException)
            {
                // A malformed or unreachable list must not stop everybody uploading, so this fails
                // open. The result is still cached so a broken blob is not re-read on every
                // request.
                logger.LogError(ex, "Could not read the device blocklist.");
            }

            memoryCache.Set(CacheKey, blocked, CacheDuration);
            return blocked;
        }
    }
}
