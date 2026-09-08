using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Tests
{
    public class BlockedDevicesDatabaseTests
    {
        private const string DeviceId = "Case-Preserved_123";

        [Fact]
        public void BlockedDeviceUsesCosmosIdAndReasonNamesAndRoundTripsPlainText()
        {
            BlockedDevice item = new BlockedDevice { DeviceId = DeviceId, Reason = "<script>plain text</script>\n\"reason\"" };

            string json = JsonConvert.SerializeObject(item);
            JObject document = JObject.Parse(json);
            Assert.Equal(new[] { "id", "reason" }, document.Properties().Select(property => property.Name));
            Assert.Equal(DeviceId, document.Value<string>("id"));
            Assert.Equal(item.Reason, document.Value<string>("reason"));
            BlockedDevice restored = JsonConvert.DeserializeObject<BlockedDevice>(json)!;
            Assert.Equal(item.DeviceId, restored.DeviceId);
            Assert.Equal(item.Reason, restored.Reason);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData(" \r\n\t", null)]
        [InlineData(" \t Case-Preserved_123 \n", DeviceId)]
        [InlineData("a b", "a b")]
        public void NormalizeOnlyTrimsAndPreservesCase(string? input, string? expected)
        {
            Assert.Equal(expected, DeviceIdRules.Normalize(input));
        }

        [Fact]
        public void ValidationAllowsOnlyAsciiLettersDigitsHyphenAndUnderscoreWithinLengthBoundaries()
        {
            for (int value = 0; value <= char.MaxValue; value++)
            {
                char character = (char)value;
                bool expected = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '-' or '_';
                Assert.Equal(expected, DeviceIdRules.IsValid(character.ToString()));
            }

            Assert.True(DeviceIdRules.IsValid(new string('a', 128)));
            Assert.False(DeviceIdRules.IsValid(new string('a', 129)));
            Assert.False(DeviceIdRules.IsValid(null));
            Assert.False(DeviceIdRules.IsValid(""));
            Assert.False(DeviceIdRules.IsValid(" a "));
        }

        public static IEnumerable<object?[]> InvalidDeviceIds()
        {
            foreach (string? id in new[] { null, "", " \t", "a/b", "a\\b", "a.b", "a?b", "a#b", "a b", "a\nb", "é", "Ａ", "😀", new string('a', 129) })
            {
                yield return new object?[] { id };
            }
        }

        [Theory]
        [MemberData(nameof(InvalidDeviceIds))]
        public async Task InvalidIdsAreRejectedBeforeAnyStorageAccess(string? deviceId)
        {
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            BlockedDevicesDatabase database = CreateDatabase(container.Object);

            await Assert.ThrowsAsync<ArgumentException>(() => database.IsBlockedAsync(deviceId!));
            await Assert.ThrowsAsync<ArgumentException>(() => database.BlockAsync(deviceId!, "reason"));
            await Assert.ThrowsAsync<ArgumentException>(() => database.UnblockAsync(deviceId!));

            container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task PointReadsUseExactIdPartitionAndCancellationWithoutCaching()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.ReadItemAsync<BlockedDevice>(DeviceId, new PartitionKey(DeviceId), null, cancellation.Token))
                .ReturnsAsync(Mock.Of<ItemResponse<BlockedDevice>>());
            string otherCase = DeviceId.ToLowerInvariant();
            container.Setup(c => c.ReadItemAsync<BlockedDevice>(otherCase, new PartitionKey(otherCase), null, cancellation.Token))
                .ThrowsAsync(StorageFailure(HttpStatusCode.NotFound));
            BlockedDevicesDatabase database = CreateDatabase(container.Object);

            Assert.True(await database.IsBlockedAsync($" \t{DeviceId}\n ", cancellation.Token));
            Assert.False(await database.IsBlockedAsync(otherCase, cancellation.Token));
            Assert.True(await database.IsBlockedAsync(DeviceId, cancellation.Token));
            Assert.False(await database.IsBlockedAsync(otherCase, cancellation.Token));

            container.Verify(c => c.ReadItemAsync<BlockedDevice>(DeviceId, new PartitionKey(DeviceId), null, cancellation.Token), Times.Exactly(2));
            container.Verify(c => c.ReadItemAsync<BlockedDevice>(otherCase, new PartitionKey(otherCase), null, cancellation.Token), Times.Exactly(2));
            container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task BlockAndUnblockAreAtomicIdempotentAndImmediatelyReflectedByFreshReads()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Dictionary<(string, PartitionKey), BlockedDevice> items = new Dictionary<(string, PartitionKey), BlockedDevice>();
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.UpsertItemAsync(It.IsAny<BlockedDevice>(), It.IsAny<PartitionKey?>(), null, cancellation.Token))
                .Callback<BlockedDevice, PartitionKey?, ItemRequestOptions?, CancellationToken>((item, partition, _, _) =>
                {
                    Assert.Equal(new PartitionKey(item.DeviceId), partition);
                    Assert.Equal(item.DeviceId, JObject.FromObject(item).Value<string>("id"));
                    Assert.False(string.IsNullOrWhiteSpace(item.Reason));
                    items[(item.DeviceId, partition!.Value)] = item;
                })
                .ReturnsAsync(Mock.Of<ItemResponse<BlockedDevice>>());
            container.Setup(c => c.ReadItemAsync<BlockedDevice>(It.IsAny<string>(), It.IsAny<PartitionKey>(), null, cancellation.Token))
                .Returns((string id, PartitionKey partition, ItemRequestOptions? _, CancellationToken _) =>
                    items.ContainsKey((id, partition))
                        ? Task.FromResult(Mock.Of<ItemResponse<BlockedDevice>>())
                        : Task.FromException<ItemResponse<BlockedDevice>>(StorageFailure(HttpStatusCode.NotFound)));
            container.Setup(c => c.DeleteItemAsync<BlockedDevice>(DeviceId, new PartitionKey(DeviceId), null, cancellation.Token))
                .Returns(() => items.Remove((DeviceId, new PartitionKey(DeviceId)))
                    ? Task.FromResult(Mock.Of<ItemResponse<BlockedDevice>>())
                    : Task.FromException<ItemResponse<BlockedDevice>>(StorageFailure(HttpStatusCode.NotFound)));
            container.Setup(c => c.ReadContainerAsync(null, cancellation.Token))
                .ReturnsAsync(Mock.Of<ContainerResponse>());
            BlockedDevicesDatabase database = CreateDatabase(container.Object);

            Assert.False(await database.IsBlockedAsync(DeviceId, cancellation.Token));
            await database.BlockAsync(DeviceId.ToLowerInvariant(), "Other installation", cancellation.Token);
            await database.BlockAsync($" {DeviceId} ", " \nRepeated unrelated photos.\t ", cancellation.Token);
            await database.BlockAsync(DeviceId, "Repeated unrelated photos.", cancellation.Token);
            Assert.Equal(2, items.Count);
            Assert.Equal("Repeated unrelated photos.", items[(DeviceId, new PartitionKey(DeviceId))].Reason);
            Assert.True(await database.IsBlockedAsync(DeviceId, cancellation.Token));

            await database.UnblockAsync($" {DeviceId} ", cancellation.Token);
            Assert.False(await database.IsBlockedAsync(DeviceId, cancellation.Token));
            await database.UnblockAsync(DeviceId, cancellation.Token);
            Assert.False(await database.IsBlockedAsync(DeviceId, cancellation.Token));
            Assert.True(await database.IsBlockedAsync(DeviceId.ToLowerInvariant(), cancellation.Token));
            Assert.Single(items);
            container.Verify(c => c.ReadContainerAsync(null, cancellation.Token), Times.Once);
            container.Verify(c => c.UpsertItemAsync(It.Is<BlockedDevice>(item =>
                item.DeviceId == DeviceId && item.Reason == "Repeated unrelated photos."),
                new PartitionKey(DeviceId), null, cancellation.Token), Times.Exactly(2));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(128, 1000)]
        public async Task BlockAcceptsTrimmedIdAndReasonBoundaries(int idLength, int reasonLength)
        {
            string id = new string('A', idLength);
            string reason = new string('r', reasonLength);
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.UpsertItemAsync(It.Is<BlockedDevice>(item => item.DeviceId == id && item.Reason == reason),
                    new PartitionKey(id), null, CancellationToken.None))
                .ReturnsAsync(Mock.Of<ItemResponse<BlockedDevice>>());

            await CreateDatabase(container.Object).BlockAsync($" \t{id}\n", $" \r\n{reason}\t ");

            container.VerifyAll();
            container.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t\n")]
        public async Task BlockRejectsMissingReasonBeforeStorageAccess(string? reason)
        {
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);

            await Assert.ThrowsAsync<ArgumentException>(() => CreateDatabase(container.Object).BlockAsync(DeviceId, reason!));

            container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task BlockRejectsOverlongTrimmedReasonBeforeStorageAccess()
        {
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);

            await Assert.ThrowsAsync<ArgumentException>(() => CreateDatabase(container.Object)
                .BlockAsync(DeviceId, " " + new string('r', 1001) + " "));

            container.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        public async Task ReadAndMutationsPropagateCosmosFailures(HttpStatusCode statusCode)
        {
            foreach (string operation in new[] { "read", "block", "unblock" })
            {
                Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
                CosmosException failure = StorageFailure(statusCode);
                SetupFailure(container, operation, failure, CancellationToken.None);

                Assert.Same(failure, await Assert.ThrowsAsync<CosmosException>(() =>
                    Invoke(CreateDatabase(container.Object), operation)));
                container.VerifyAll();
            }
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        public async Task Delete404DoesNotAcknowledgeAbsenceWhenContainerCannotBeRead(HttpStatusCode statusCode)
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            CosmosException failure = StorageFailure(statusCode);
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.DeleteItemAsync<BlockedDevice>(DeviceId, new PartitionKey(DeviceId), null, cancellation.Token))
                .ThrowsAsync(StorageFailure(HttpStatusCode.NotFound));
            container.Setup(c => c.ReadContainerAsync(null, cancellation.Token)).ThrowsAsync(failure);

            Assert.Same(failure, await Assert.ThrowsAsync<CosmosException>(() =>
                CreateDatabase(container.Object).UnblockAsync(DeviceId, cancellation.Token)));
            container.VerifyAll();
            container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ListDrainsEveryPageIncludingEmptyPagesAndReturnsStoredReasons()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            BlockedDevice first = new BlockedDevice { DeviceId = DeviceId, Reason = "<b>Not markup</b>" };
            BlockedDevice last = new BlockedDevice { DeviceId = "other", Reason = "Still available without a source report" };
            Queue<BlockedDevice[]> pages = new Queue<BlockedDevice[]>(new[] { new[] { first }, Array.Empty<BlockedDevice>(), new[] { last } });
            Mock<FeedIterator<BlockedDevice>> iterator = new Mock<FeedIterator<BlockedDevice>>();
            iterator.SetupGet(i => i.HasMoreResults).Returns(() => pages.Count > 0);
            iterator.Setup(i => i.ReadNextAsync(cancellation.Token)).ReturnsAsync(() => Page(pages.Dequeue()));
            Mock<Container> container = QueryContainer(iterator);

            IReadOnlyList<BlockedDevice> results = await CreateDatabase(container.Object).ListAsync(cancellation.Token);

            Assert.Equal(new[] { first, last }, results);
            Assert.Equal(first.Reason, results[0].Reason);
            Assert.Equal(last.Reason, results[1].Reason);
            iterator.Verify(i => i.ReadNextAsync(cancellation.Token), Times.Exactly(3));
            iterator.Protected().Verify("Dispose", Times.Once(), new object[] { true });
            container.VerifyAll();
            container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ListReturnsEmptyOnlyWhenTheQuerySucceeds()
        {
            Mock<FeedIterator<BlockedDevice>> iterator = new Mock<FeedIterator<BlockedDevice>>();
            iterator.SetupSequence(i => i.HasMoreResults).Returns(true).Returns(false);
            iterator.Setup(i => i.ReadNextAsync(CancellationToken.None)).ReturnsAsync(Page());

            Assert.Empty(await CreateDatabase(QueryContainer(iterator).Object).ListAsync());
            iterator.Verify(i => i.ReadNextAsync(CancellationToken.None), Times.Once);
        }

        [Theory]
        [InlineData(false, HttpStatusCode.NotFound)]
        [InlineData(false, HttpStatusCode.Forbidden)]
        [InlineData(true, HttpStatusCode.ServiceUnavailable)]
        [InlineData(true, HttpStatusCode.TooManyRequests)]
        public async Task ListPropagatesFailuresWithoutReturningEmptyOrPartialResults(bool afterFirstPage, HttpStatusCode statusCode)
        {
            CosmosException failure = StorageFailure(statusCode);
            Mock<FeedIterator<BlockedDevice>> iterator = new Mock<FeedIterator<BlockedDevice>>();
            iterator.SetupGet(i => i.HasMoreResults).Returns(true);
            var sequence = iterator.SetupSequence(i => i.ReadNextAsync(CancellationToken.None));
            if (afterFirstPage)
            {
                sequence.ReturnsAsync(Page(new BlockedDevice { DeviceId = DeviceId, Reason = "Not a partial result" }));
            }
            sequence.ThrowsAsync(failure);

            Assert.Same(failure, await Assert.ThrowsAsync<CosmosException>(() =>
                CreateDatabase(QueryContainer(iterator).Object).ListAsync()));
            iterator.Verify(i => i.ReadNextAsync(CancellationToken.None), Times.Exactly(afterFirstPage ? 2 : 1));
        }

        [Theory]
        [InlineData("read")]
        [InlineData("block")]
        [InlineData("unblock")]
        [InlineData("list")]
        public async Task PreCanceledOperationsDoNotAccessStorage(string operation)
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);

            var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                Invoke(CreateDatabase(container.Object), operation, cancellation.Token));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            container.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("read")]
        [InlineData("block")]
        [InlineData("unblock")]
        [InlineData("list")]
        public async Task InFlightCancellationAndUnexpectedFailuresAreNotSwallowed(string operation)
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            foreach (Exception failure in new Exception[]
            {
                new OperationCanceledException(cancellation.Token), new HttpRequestException("offline"),
                new TimeoutException("timeout"), new InvalidOperationException("programming failure")
            })
            {
                Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
                SetupFailure(container, operation, failure, cancellation.Token);

                Exception? actual = await Record.ExceptionAsync(() =>
                    Invoke(CreateDatabase(container.Object), operation, cancellation.Token));

                Assert.Same(failure, actual);
                container.VerifyAll();
            }
        }

        [Fact]
        public async Task ListCancellationBetweenPagesDoesNotReturnPartialResults()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Mock<FeedIterator<BlockedDevice>> iterator = new Mock<FeedIterator<BlockedDevice>>();
            iterator.SetupGet(i => i.HasMoreResults).Returns(true);
            iterator.Setup(i => i.ReadNextAsync(cancellation.Token)).ReturnsAsync(() =>
            {
                cancellation.Cancel();
                return Page(new BlockedDevice { DeviceId = DeviceId, Reason = "Partial" });
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                CreateDatabase(QueryContainer(iterator).Object).ListAsync(cancellation.Token));

            iterator.Verify(i => i.ReadNextAsync(cancellation.Token), Times.Once);
        }

        [Fact]
        public async Task Delete404ContainerVerificationPropagatesCancellation()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            OperationCanceledException failure = new OperationCanceledException(cancellation.Token);
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.DeleteItemAsync<BlockedDevice>(DeviceId, new PartitionKey(DeviceId), null, cancellation.Token))
                .ThrowsAsync(StorageFailure(HttpStatusCode.NotFound));
            container.Setup(c => c.ReadContainerAsync(null, cancellation.Token)).ThrowsAsync(failure);

            Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() =>
                CreateDatabase(container.Object).UnblockAsync(DeviceId, cancellation.Token)));
            container.VerifyAll();
        }

        private static BlockedDevicesDatabase CreateDatabase(Container container) =>
            new BlockedDevicesDatabase(NullLogger<BlockedDevicesDatabase>.Instance, container);

        private static CosmosException StorageFailure(HttpStatusCode statusCode) =>
            new CosmosException("Storage failure", statusCode, 0, "test", 0);

        private static FeedResponse<BlockedDevice> Page(params BlockedDevice[] items)
        {
            Mock<FeedResponse<BlockedDevice>> page = new Mock<FeedResponse<BlockedDevice>>();
            page.Setup(p => p.GetEnumerator()).Returns(() => ((IEnumerable<BlockedDevice>)items).GetEnumerator());
            return page.Object;
        }

        private static Mock<Container> QueryContainer(Mock<FeedIterator<BlockedDevice>> iterator)
        {
            Mock<Container> container = new Mock<Container>(MockBehavior.Strict);
            container.Setup(c => c.GetItemQueryIterator<BlockedDevice>(
                    It.Is<QueryDefinition>(query => query.QueryText == "SELECT c.id, c.reason FROM c"), null, null))
                .Returns(iterator.Object);
            return container;
        }

        private static Task Invoke(BlockedDevicesDatabase database, string operation, CancellationToken cancellationToken = default) =>
            operation switch
            {
                "read" => database.IsBlockedAsync(DeviceId, cancellationToken),
                "block" => database.BlockAsync(DeviceId, "reason", cancellationToken),
                "unblock" => database.UnblockAsync(DeviceId, cancellationToken),
                "list" => database.ListAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };

        private static void SetupFailure(Mock<Container> container, string operation, Exception failure, CancellationToken cancellationToken)
        {
            switch (operation)
            {
                case "read":
                    container.Setup(c => c.ReadItemAsync<BlockedDevice>(DeviceId, new PartitionKey(DeviceId), null, cancellationToken))
                        .ThrowsAsync(failure);
                    break;
                case "block":
                    container.Setup(c => c.UpsertItemAsync(It.IsAny<BlockedDevice>(), new PartitionKey(DeviceId), null, cancellationToken))
                        .ThrowsAsync(failure);
                    break;
                case "unblock":
                    container.Setup(c => c.DeleteItemAsync<BlockedDevice>(DeviceId, new PartitionKey(DeviceId), null, cancellationToken))
                        .ThrowsAsync(failure);
                    break;
                case "list":
                    Mock<FeedIterator<BlockedDevice>> iterator = new Mock<FeedIterator<BlockedDevice>>();
                    iterator.SetupGet(i => i.HasMoreResults).Returns(true);
                    iterator.Setup(i => i.ReadNextAsync(cancellationToken)).ThrowsAsync(failure);
                    container.Setup(c => c.GetItemQueryIterator<BlockedDevice>(It.IsAny<QueryDefinition>(), null, null))
                        .Returns(iterator.Object);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }
    }
}
