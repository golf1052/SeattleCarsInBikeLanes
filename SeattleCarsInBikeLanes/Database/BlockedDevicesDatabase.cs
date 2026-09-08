using System.Net;
using Microsoft.Azure.Cosmos;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Database
{
    public class BlockedDevicesDatabase
    {
        public const int MaximumReasonLength = 1000;
        private readonly Container container;

        public BlockedDevicesDatabase(ILogger<BlockedDevicesDatabase> logger, Container container)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(container);
            this.container = container;
        }

        public virtual async Task<bool> IsBlockedAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            deviceId = ValidateDeviceId(deviceId);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await container.ReadItemAsync<BlockedDevice>(deviceId, new PartitionKey(deviceId),
                    cancellationToken: cancellationToken);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }

        public virtual async Task BlockAsync(string deviceId, string reason, CancellationToken cancellationToken = default)
        {
            deviceId = ValidateDeviceId(deviceId);
            string? normalizedReason = reason?.Trim();
            if (string.IsNullOrEmpty(normalizedReason) || normalizedReason.Length > MaximumReasonLength)
            {
                throw new ArgumentException($"A reason of 1-{MaximumReasonLength} characters is required.", nameof(reason));
            }

            cancellationToken.ThrowIfCancellationRequested();
            await container.UpsertItemAsync(new BlockedDevice { DeviceId = deviceId, Reason = normalizedReason },
                new PartitionKey(deviceId), cancellationToken: cancellationToken);
        }

        public virtual async Task UnblockAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            deviceId = ValidateDeviceId(deviceId);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await container.DeleteItemAsync<BlockedDevice>(deviceId, new PartitionKey(deviceId),
                    cancellationToken: cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A delete 404 can also mean the container itself is unavailable.
                await container.ReadContainerAsync(cancellationToken: cancellationToken);
            }
        }

        public virtual async Task<IReadOnlyList<BlockedDevice>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FeedIterator<BlockedDevice> iterator = container.GetItemQueryIterator<BlockedDevice>(
                new QueryDefinition("SELECT c.id, c.reason FROM c"));
            List<BlockedDevice> blockedDevices = new List<BlockedDevice>();
            while (iterator.HasMoreResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FeedResponse<BlockedDevice> page = await iterator.ReadNextAsync(cancellationToken);
                blockedDevices.AddRange(page);
            }

            return blockedDevices;
        }

        private static string ValidateDeviceId(string deviceId)
        {
            string? normalizedId = DeviceIdRules.Normalize(deviceId);
            if (!DeviceIdRules.IsValid(normalizedId))
            {
                throw new ArgumentException("A device ID of 1-128 ASCII letters, digits, hyphens or underscores is required.",
                    nameof(deviceId));
            }

            return normalizedId!;
        }
    }
}
