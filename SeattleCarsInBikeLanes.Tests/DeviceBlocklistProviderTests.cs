using System.Net;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Tests
{
    public class DeviceBlocklistProviderTests
    {
        private sealed class Fixture
        {
            public Mock<Container> Container { get; } = new Mock<Container>(MockBehavior.Strict);
            public Mock<ILogger<DeviceBlocklistProvider>> Log { get; } = new Mock<ILogger<DeviceBlocklistProvider>>();
            public HashSet<string> Blocked { get; } = new HashSet<string>(StringComparer.Ordinal);
            public DeviceBlocklistProvider Provider { get; }

            public Fixture()
            {
                Container.Setup(c => c.ReadItemAsync<BlockedDevice>(It.IsAny<string>(), It.IsAny<PartitionKey>(),
                        null, It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string id, PartitionKey partition, ItemRequestOptions? _, CancellationToken _) =>
                    {
                        Assert.Equal(new PartitionKey(id), partition);
                        if (!Blocked.Contains(id))
                        {
                            throw new CosmosException("Not found", HttpStatusCode.NotFound, 0, "test", 0);
                        }
                        Mock<ItemResponse<BlockedDevice>> response = new Mock<ItemResponse<BlockedDevice>>();
                        response.SetupGet(r => r.Resource)
                            .Returns(new BlockedDevice() { DeviceId = id, Reason = "Private moderation reason" });
                        return response.Object;
                    });
                Provider = new DeviceBlocklistProvider(Log.Object,
                    new BlockedDevicesDatabase(NullLogger<BlockedDevicesDatabase>.Instance, Container.Object));
            }

            public void Fail(Exception failure)
            {
                Container.Setup(c => c.ReadItemAsync<BlockedDevice>(It.IsAny<string>(), It.IsAny<PartitionKey>(),
                        null, It.IsAny<CancellationToken>()))
                    .ThrowsAsync(failure);
            }
        }

        [Fact]
        public async Task UsesExactDeviceIdAndPartitionForPointReads()
        {
            Fixture fixture = new Fixture();
            fixture.Blocked.Add("device-1");

            Assert.True(await fixture.Provider.IsBlocked("device-1"));
            Assert.False(await fixture.Provider.IsBlocked("DEVICE-1"));
            Assert.False(await fixture.Provider.IsBlocked("device-10"));

            foreach (string id in new[] { "device-1", "DEVICE-1", "device-10" })
            {
                fixture.Container.Verify(c => c.ReadItemAsync<BlockedDevice>(
                    id, new PartitionKey(id), null, CancellationToken.None), Times.Once);
            }
            fixture.Container.VerifyNoOtherCalls();
            Assert.Empty(fixture.Log.Invocations);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task NoDeviceIdDoesNotAccessStorage(string? deviceId)
        {
            Fixture fixture = new Fixture();

            Assert.False(await fixture.Provider.IsBlocked(deviceId));

            fixture.Container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ReadsEveryTimeAndObservesBlockAndUnblockWithoutCaching()
        {
            Fixture fixture = new Fixture();

            Assert.False(await fixture.Provider.IsBlocked("device-1"));
            fixture.Blocked.Add("device-1");
            Assert.True(await fixture.Provider.IsBlocked("device-1"));
            fixture.Blocked.Remove("device-1");
            Assert.False(await fixture.Provider.IsBlocked("device-1"));

            fixture.Container.Verify(c => c.ReadItemAsync<BlockedDevice>("device-1",
                new PartitionKey("device-1"), null, CancellationToken.None), Times.Exactly(3));
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.RequestTimeout)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        public async Task LogsAndFailsOpenOnCosmosFailureWithoutCaching(HttpStatusCode status)
        {
            Fixture fixture = new Fixture();
            fixture.Fail(new CosmosException("Unavailable", status, 0, "test", 0));

            Assert.False(await fixture.Provider.IsBlocked("device-1"));
            Assert.False(await fixture.Provider.IsBlocked("device-1"));

            fixture.Container.Verify(c => c.ReadItemAsync<BlockedDevice>("device-1",
                new PartitionKey("device-1"), null, CancellationToken.None), Times.Exactly(2));
            Assert.Equal(2, fixture.Log.Invocations.Count(i => i.Method.Name == "Log" &&
                i.Arguments[0].Equals(LogLevel.Error)));
        }

        public static IEnumerable<object[]> StorageFailures()
        {
            yield return [new HttpRequestException("Network unavailable")];
            yield return [new TimeoutException("Request timed out")];
            yield return [new OperationCanceledException("SDK timeout")];
            yield return [new AuthenticationFailedException("Azure credentials unavailable")];
            yield return [new JsonReaderException("Unreadable document")];
        }

        [Theory]
        [MemberData(nameof(StorageFailures))]
        public async Task LogsAndFailsOpenOnExpectedStorageFailure(Exception failure)
        {
            Fixture fixture = new Fixture();
            fixture.Fail(failure);

            Assert.False(await fixture.Provider.IsBlocked("device-1"));

            Assert.Contains(fixture.Log.Invocations, i => i.Method.Name == "Log" &&
                i.Arguments[0].Equals(LogLevel.Error) && ReferenceEquals(failure, i.Arguments[3]));
        }

        [Fact]
        public async Task DoesNotCacheFailureWhenStorageRecovers()
        {
            Fixture fixture = new Fixture();
            fixture.Container.SetupSequence(c => c.ReadItemAsync<BlockedDevice>("device-1",
                    new PartitionKey("device-1"), null, CancellationToken.None))
                .ThrowsAsync(new CosmosException("Unavailable", HttpStatusCode.ServiceUnavailable, 0, "test", 0))
                .ReturnsAsync(Mock.Of<ItemResponse<BlockedDevice>>(r =>
                    r.Resource == new BlockedDevice() { DeviceId = "device-1", Reason = "Private reason" }));

            Assert.False(await fixture.Provider.IsBlocked("device-1"));
            Assert.True(await fixture.Provider.IsBlocked("device-1"));
        }

        [Fact]
        public async Task ForwardsCancellationToken()
        {
            Fixture fixture = new Fixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();

            Assert.False(await fixture.Provider.IsBlocked("device-1", cancellation.Token));

            fixture.Container.Verify(c => c.ReadItemAsync<BlockedDevice>("device-1",
                new PartitionKey("device-1"), null, cancellation.Token), Times.Once);
        }

        [Fact]
        public async Task AlreadyCanceledRequestDoesNotAccessStorage()
        {
            Fixture fixture = new Fixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Provider.IsBlocked("device-1", cancellation.Token));

            fixture.Container.VerifyNoOtherCalls();
            Assert.Empty(fixture.Log.Invocations);
        }

        [Fact]
        public async Task CallerCancellationDuringReadIsNotAnAllowDecision()
        {
            Fixture fixture = new Fixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            fixture.Container.Setup(c => c.ReadItemAsync<BlockedDevice>("device-1",
                    new PartitionKey("device-1"), null, cancellation.Token))
                .Callback(cancellation.Cancel)
                .ThrowsAsync(new OperationCanceledException(cancellation.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Provider.IsBlocked("device-1", cancellation.Token));

            Assert.Empty(fixture.Log.Invocations);
        }

        [Fact]
        public async Task UnexpectedErrorsAreNotSwallowed()
        {
            Fixture fixture = new Fixture();
            InvalidOperationException failure = new InvalidOperationException("Programming error");
            fixture.Fail(failure);

            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Provider.IsBlocked("device-1")));
            Assert.Empty(fixture.Log.Invocations);
        }
    }
}
