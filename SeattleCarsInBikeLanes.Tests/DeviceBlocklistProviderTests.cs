using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Tests
{
    public class DeviceBlocklistProviderTests
    {
        private static DeviceBlocklistProvider CreateProvider(BlobContainerClient containerClient)
        {
            return new DeviceBlocklistProvider(NullLogger<DeviceBlocklistProvider>.Instance,
                containerClient,
                new MemoryCache(new MemoryCacheOptions()));
        }

        private static Mock<BlobClient> CreateBlobClient(string? json, Exception? failure = null)
        {
            Mock<BlobClient> blobClient = new Mock<BlobClient>();

            if (failure != null)
            {
                blobClient.Setup(c => c.DownloadContentAsync()).ThrowsAsync(failure);
            }
            else
            {
                BlobDownloadResult download = BlobsModelFactory.BlobDownloadResult(BinaryData.FromString(json!));
                blobClient.Setup(c => c.DownloadContentAsync())
                    .ReturnsAsync(Response.FromValue(download, Mock.Of<Response>()));
            }

            return blobClient;
        }

        private static BlobContainerClient CreateContainer(Mock<BlobClient> blobClient)
        {
            Mock<BlobContainerClient> containerClient = new Mock<BlobContainerClient>();
            containerClient.Setup(c => c.GetBlobClient(DeviceBlocklistProvider.BlobName))
                .Returns(blobClient.Object);

            return containerClient.Object;
        }

        private static BlobContainerClient CreateContainer(string? json, Exception? failure = null) =>
            CreateContainer(CreateBlobClient(json, failure));

        [Fact]
        public async Task BlocksAListedDevice()
        {
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer("[\"device-1\",\"device-2\"]"));

            Assert.True(await provider.IsBlocked("device-1"));
        }

        [Fact]
        public async Task AllowsADeviceThatIsNotListed()
        {
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer("[\"device-1\"]"));

            Assert.False(await provider.IsBlocked("device-3"));
        }

        [Fact]
        public async Task MatchesDeviceIdsExactly()
        {
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer("[\"device-1\"]"));

            // Device ids are opaque, so a near miss is a different device.
            Assert.False(await provider.IsBlocked("DEVICE-1"));
            Assert.False(await provider.IsBlocked("device-10"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task AllowsRequestsWithNoDeviceId(string? deviceId)
        {
            // The website sends no device id, so treating a missing one as blocked would take
            // uploads away from every browser.
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer("[\"device-1\"]"));

            Assert.False(await provider.IsBlocked(deviceId));
        }

        [Fact]
        public async Task AllowsEverythingWhenTheListDoesNotExist()
        {
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer(null,
                new RequestFailedException(404, "BlobNotFound")));

            Assert.False(await provider.IsBlocked("device-1"));
        }

        [Fact]
        public async Task FailsOpenWhenTheListCannotBeRead()
        {
            // A broken blocklist must not stop everybody uploading.
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer(null,
                new RequestFailedException(503, "storage is unavailable")));

            Assert.False(await provider.IsBlocked("device-1"));
        }

        [Fact]
        public async Task FailsOpenWhenTheListIsNotValidJson()
        {
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer("not json"));

            Assert.False(await provider.IsBlocked("device-1"));
        }

        [Fact]
        public async Task IgnoresBlankEntries()
        {
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer("[\"\",\"  \",\"device-1\"]"));

            Assert.True(await provider.IsBlocked("device-1"));
            Assert.False(await provider.IsBlocked("  "));
        }

        [Fact]
        public async Task ReadsTheListOnlyOnceWithinTheCacheWindow()
        {
            Mock<BlobClient> blobClient = CreateBlobClient("[\"device-1\"]");
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer(blobClient));

            await provider.IsBlocked("device-1");
            await provider.IsBlocked("device-2");
            await provider.IsBlocked("device-3");

            // Every upload checks the list, so it has to be cached rather than fetched each time.
            blobClient.Verify(c => c.DownloadContentAsync(), Times.Once);
        }

        [Fact]
        public async Task CachesTheEmptyResultAfterAFailure()
        {
            Mock<BlobClient> blobClient = CreateBlobClient(null, new RequestFailedException(503, "unavailable"));
            DeviceBlocklistProvider provider = CreateProvider(CreateContainer(blobClient));

            await provider.IsBlocked("device-1");
            await provider.IsBlocked("device-2");

            // A failing blob should not be retried on every single upload.
            blobClient.Verify(c => c.DownloadContentAsync(), Times.Once);
        }
    }
}
