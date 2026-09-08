using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;

namespace SeattleCarsInBikeLanes.Tests
{
    public class ReportedItemsDatabaseTests
    {
        private const string ReportId = "abcdef0123456789abcdef0123456789.0";

        private static ReportedItemsDatabase CreateDatabase(Container container)
        {
            return new ReportedItemsDatabase(NullLogger<ReportedItemsDatabase>.Instance, container);
        }

        [Fact]
        public async Task SavePublishedReportAsync_ReplacesTheSameIdAndPartitionOnRetry()
        {
            Dictionary<(string Id, PartitionKey PartitionKey), ReportedItem> savedItems = new Dictionary<(string Id, PartitionKey PartitionKey), ReportedItem>();
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.UpsertItemAsync(It.IsAny<ReportedItem>(), It.IsAny<PartitionKey?>(),
                    null, It.IsAny<CancellationToken>()))
                .Callback<ReportedItem, PartitionKey?, ItemRequestOptions?, CancellationToken>((item, partitionKey, _, _) =>
                {
                    Assert.Equal(new PartitionKey(item.TweetId), partitionKey);
                    Assert.Equal(item.TweetId, JObject.FromObject(item).Value<string>("id"));
                    savedItems[(item.TweetId, partitionKey!.Value)] = item;
                })
                .ReturnsAsync(Mock.Of<ItemResponse<ReportedItem>>());
            ReportedItemsDatabase database = CreateDatabase(container.Object);
            ReportedItem original = new ReportedItem() { TweetId = ReportId, ThreadsLink = "https://www.threads.net/@test/post/old" };
            ReportedItem updated = new ReportedItem() { TweetId = ReportId, ThreadsLink = "https://www.threads.net/@test/post/new" };
            ReportedItem unrelated = new ReportedItem() { TweetId = "unrelated.0" };

            await database.SavePublishedReportAsync(unrelated);
            await database.SavePublishedReportAsync(original);
            await database.SavePublishedReportAsync(updated);
            await database.SavePublishedReportAsync(updated);

            Assert.Equal(2, savedItems.Count);
            Assert.Same(updated, savedItems[(ReportId, new PartitionKey(ReportId))]);
            Assert.Same(unrelated, savedItems[(unrelated.TweetId, new PartitionKey(unrelated.TweetId))]);
            container.Verify(c => c.UpsertItemAsync(updated, new PartitionKey(ReportId), null, CancellationToken.None),
                Times.Exactly(2));
        }

        [Fact]
        public async Task SavePublishedReportAsync_ForwardsCancellationToken()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            ReportedItem item = new ReportedItem() { TweetId = ReportId };
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.UpsertItemAsync(item, new PartitionKey(ReportId), null, cancellation.Token))
                .ReturnsAsync(Mock.Of<ItemResponse<ReportedItem>>());

            await CreateDatabase(container.Object).SavePublishedReportAsync(item, cancellation.Token);

            container.Verify(c => c.UpsertItemAsync(item, new PartitionKey(ReportId), null, cancellation.Token), Times.Once);
        }

        [Fact]
        public async Task SavePublishedReportAsync_PropagatesCancellation()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            ReportedItem item = new ReportedItem() { TweetId = ReportId };
            OperationCanceledException failure = new OperationCanceledException(cancellation.Token);
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.UpsertItemAsync(item, new PartitionKey(ReportId), null, cancellation.Token))
                .ThrowsAsync(failure);

            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                CreateDatabase(container.Object).SavePublishedReportAsync(item, cancellation.Token));

            Assert.Same(failure, exception);
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        public async Task SavePublishedReportAsync_PropagatesStorageFailures(HttpStatusCode statusCode)
        {
            ReportedItem item = new ReportedItem() { TweetId = ReportId };
            CosmosException failure = new CosmosException("Storage unavailable", statusCode, 0, "test", 0);
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.UpsertItemAsync(item, new PartitionKey(ReportId), null, CancellationToken.None))
                .ThrowsAsync(failure);

            var exception = await Assert.ThrowsAsync<CosmosException>(() =>
                CreateDatabase(container.Object).SavePublishedReportAsync(item));

            Assert.Same(failure, exception);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t")]
        public async Task SavePublishedReportAsync_RejectsMissingId(string? id)
        {
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);

            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                CreateDatabase(container.Object).SavePublishedReportAsync(new ReportedItem() { TweetId = id! }));

            container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SavePublishedReportAsync_RejectsNullItem()
        {
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                CreateDatabase(container.Object).SavePublishedReportAsync(null!));

            container.VerifyNoOtherCalls();
        }
    }
}
