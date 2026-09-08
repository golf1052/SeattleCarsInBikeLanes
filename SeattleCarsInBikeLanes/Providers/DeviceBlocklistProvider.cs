using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using SeattleCarsInBikeLanes.Database;

namespace SeattleCarsInBikeLanes.Providers
{
    /// <summary>
    /// The set of devices that are not allowed to submit reports.
    /// </summary>
    public class DeviceBlocklistProvider
    {
        private readonly ILogger<DeviceBlocklistProvider> logger;
        private readonly BlockedDevicesDatabase database;

        public DeviceBlocklistProvider(ILogger<DeviceBlocklistProvider> logger,
            BlockedDevicesDatabase database)
        {
            this.logger = logger;
            this.database = database;
        }

        public async Task<bool> IsBlocked(string? deviceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                // Website uploads have no installation identity.
                return false;
            }

            try
            {
                return await database.IsBlockedAsync(deviceId, cancellationToken);
            }
            catch (Exception ex) when (ex is CosmosException or HttpRequestException or TimeoutException or
                AuthenticationFailedException or JsonException ||
                ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Submission checks fail open; administrative operations must surface failures.
                logger.LogError(ex, "Could not check the device blocklist. Allowing this submission.");
                return false;
            }
        }
    }
}
