using System.ServiceModel.Syndication;
using System.Text;
using System.Xml;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Tests
{
    public class FeedProviderTests
    {
        private const string ReportId = "abcdef0123456789abcdef0123456789.0";
        private static readonly DateTime CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task AddReportedItemToFeed_ReplacesDuplicatesInEachFeedIndependently()
        {
            MemoryFeedBlob rss = new MemoryFeedBlob(true,
                                        [FeedItem("rss-first"), FeedItem(ReportId), FeedItem(ReportId), FeedItem("rss-last")]);
            MemoryFeedBlob atom = new MemoryFeedBlob(false,
                [FeedItem(ReportId), FeedItem("atom-only"), FeedItem(ReportId)]);
            FeedProvider provider = CreateProvider(rss, atom);

            await provider.AddReportedItemToFeed(Report("updated"));

            Assert.Equal(new[] { ReportId, "rss-first", "rss-last" }, rss.ReadItems().Select(i => i.Id));
            Assert.Equal(new[] { ReportId, "atom-only" }, atom.ReadItems().Select(i => i.Id));
            AssertUpdatedItem(rss.ReadItems()[0], "updated");
            AssertUpdatedItem(atom.ReadItems()[0], "updated");
            Assert.Equal("rss-first", rss.ReadItems()[1].Title.Text);
            Assert.Equal("atom-only", atom.ReadItems()[1].Title.Text);
            Assert.Equal(FeedProvider.RssContentType, rss.ContentType);
            Assert.Equal(FeedProvider.AtomContentType, atom.ContentType);
        }

        [Fact]
        public async Task AddReportedItemToFeed_RepeatedRetriesReplaceSocialAndImageUrls()
        {
            MemoryFeedBlob rss = new MemoryFeedBlob(true, []);
            MemoryFeedBlob atom = new MemoryFeedBlob(false, []);
            FeedProvider provider = CreateProvider(rss, atom);

            await provider.AddReportedItemToFeed(Report("original"));
            await provider.AddReportedItemToFeed(Report("updated"));
            await provider.AddReportedItemToFeed(Report("updated"));

            AssertUpdatedItem(Assert.Single(rss.ReadItems()), "updated");
            AssertUpdatedItem(Assert.Single(atom.ReadItems()), "updated");
            Assert.DoesNotContain("original", Html(rss.ReadItems()[0]));
            Assert.DoesNotContain("original", Html(atom.ReadItems()[0]));
        }

        [Fact]
        public async Task AddReportedItemToFeed_RetryAfterAtomWriteFailureDoesNotDuplicateRssOrEvictExtraItems()
        {
            var existing = Enumerable.Range(0, 100).Select(i => FeedItem($"existing-{i}")).ToList();
            MemoryFeedBlob rss = new MemoryFeedBlob(true, existing);
            MemoryFeedBlob atom = new MemoryFeedBlob(false, existing);
            RequestFailedException failure = new RequestFailedException(503, "Atom upload failed");
            atom.UploadFailure = failure;
            FeedProvider provider = CreateProvider(rss, atom);

            var exception = await Assert.ThrowsAsync<RequestFailedException>(() =>
                provider.AddReportedItemToFeed(Report("original")));

            Assert.Same(failure, exception);
            AssertUpdatedItem(rss.ReadItems()[0], "original");
            Assert.DoesNotContain(atom.ReadItems(), i => i.Id == ReportId);
            Assert.Equal(100, atom.ReadItems().Count);
            Assert.Equal(1, rss.UploadAttempts);
            Assert.Equal(1, atom.UploadAttempts);

            atom.UploadFailure = null;
            await provider.AddReportedItemToFeed(Report("updated"));
            await provider.AddReportedItemToFeed(Report("updated"));

            var expectedIds = new[] { ReportId }.Concat(Enumerable.Range(0, 99).Select(i => $"existing-{i}"));
            Assert.Equal(expectedIds, rss.ReadItems().Select(i => i.Id));
            Assert.Equal(expectedIds, atom.ReadItems().Select(i => i.Id));
            AssertUpdatedItem(rss.ReadItems()[0], "updated");
            AssertUpdatedItem(atom.ReadItems()[0], "updated");
            Assert.DoesNotContain("original", Html(rss.ReadItems()[0]));
            Assert.Equal(3, rss.UploadAttempts);
            Assert.Equal(3, atom.UploadAttempts);
        }

        [Fact]
        public async Task AddReportedItemToFeed_PropagatesRssWriteFailureWithoutWritingAtom()
        {
            MemoryFeedBlob rss = new MemoryFeedBlob(true, [FeedItem("rss-only")]);
            MemoryFeedBlob atom = new MemoryFeedBlob(false, [FeedItem("atom-only")]);
            RequestFailedException failure = new RequestFailedException(503, "RSS upload failed");
            rss.UploadFailure = failure;

            var exception = await Assert.ThrowsAsync<RequestFailedException>(() =>
                CreateProvider(rss, atom).AddReportedItemToFeed(Report("updated")));

            Assert.Same(failure, exception);
            Assert.Equal("rss-only", Assert.Single(rss.ReadItems()).Id);
            Assert.Equal("atom-only", Assert.Single(atom.ReadItems()).Id);
            Assert.Equal(1, rss.UploadAttempts);
            Assert.Equal(0, atom.UploadAttempts);
        }

        [Theory]
        [InlineData(99)]
        [InlineData(100)]
        [InlineData(105)]
        public async Task AddReportedItemToFeed_PreservesUnrelatedItemsWithinTheHundredItemCap(int count)
        {
            var existing = Enumerable.Range(0, count).Select(i => FeedItem($"existing-{i}")).ToList();
            MemoryFeedBlob rss = new MemoryFeedBlob(true, existing);
            MemoryFeedBlob atom = new MemoryFeedBlob(false, existing);

            await CreateProvider(rss, atom).AddReportedItemToFeed(Report("updated"));

            var expectedIds = new[] { ReportId }.Concat(Enumerable.Range(0, 99).Select(i => $"existing-{i}"));
            Assert.Equal(expectedIds, rss.ReadItems().Select(i => i.Id));
            Assert.Equal(expectedIds, atom.ReadItems().Select(i => i.Id));
        }

        [Theory]
        [InlineData(ReportId)]
        [InlineData("12345678901234567890123456789012.0")]
        [InlineData("00000000000000000000000000000001.0")]
        [InlineData("abcdef01-2345-6789-abcd-ef0123456789.0")]
        [InlineData("not-a-tweet.0")]
        [InlineData("+123456789.0")]
        [InlineData("-123456789.0")]
        [InlineData(" 123456789.0")]
        [InlineData("123,456,789.0")]
        [InlineData("1e9.0")]
        [InlineData("１２３４５６７８９.0")]
        [InlineData("18446744073709551616.0")]
        public async Task AddReportedItemToFeed_DoesNotInventTwitterLinksForNonSnowflakeIds(string id)
        {
            MemoryFeedBlob rss = new MemoryFeedBlob(true, []);
            MemoryFeedBlob atom = new MemoryFeedBlob(false, []);
            ReportedItem report = Report("updated");
            report.TweetId = id;

            await CreateProvider(rss, atom).AddReportedItemToFeed(report);

            Assert.DoesNotContain("Twitter post", Html(Assert.Single(rss.ReadItems())));
            Assert.DoesNotContain("twitter.com", Html(rss.ReadItems()[0]));
            Assert.DoesNotContain("Twitter post", Html(Assert.Single(atom.ReadItems())));
            Assert.DoesNotContain("twitter.com", Html(atom.ReadItems()[0]));
        }

        [Theory]
        [InlineData("1234567890123456789")]
        [InlineData("1234567890123456789.0")]
        [InlineData("1234567890123456789.2")]
        public async Task AddReportedItemToFeed_PreservesLegacyNumericTwitterLinks(string id)
        {
            MemoryFeedBlob rss = new MemoryFeedBlob(true, []);
            MemoryFeedBlob atom = new MemoryFeedBlob(false, []);
            ReportedItem report = Report("updated");
            report.TweetId = id;

            await CreateProvider(rss, atom).AddReportedItemToFeed(report);

            const string expectedLink = "https://twitter.com/carbikelanesea/status/1234567890123456789";
            Assert.Contains(expectedLink, Html(Assert.Single(rss.ReadItems())));
            Assert.Contains(expectedLink, Html(Assert.Single(atom.ReadItems())));
        }

        [Theory]
        [InlineData(ReportId)]
        [InlineData("1234567890123456789.0")]
        public async Task AddReportedItemToFeed_PreservesExplicitTwitterLinkInsteadOfLegacyFallback(string id)
        {
            MemoryFeedBlob rss = new MemoryFeedBlob(true, []);
            MemoryFeedBlob atom = new MemoryFeedBlob(false, []);
            ReportedItem report = Report("updated");
            report.TweetId = id;
            report.TwitterLink = "https://twitter.com/explicit/status/987654321";

            await CreateProvider(rss, atom).AddReportedItemToFeed(report);

            Assert.Contains(report.TwitterLink, Html(Assert.Single(rss.ReadItems())));
            Assert.Contains(report.TwitterLink, Html(Assert.Single(atom.ReadItems())));
            Assert.DoesNotContain("carbikelanesea/status", Html(rss.ReadItems()[0]));
            Assert.DoesNotContain("carbikelanesea/status", Html(atom.ReadItems()[0]));
        }

        private static ReportedItem Report(string version)
        {
            return new ReportedItem()
            {
                TweetId = ReportId,
                CreatedAt = CreatedAt,
                NumberOfCars = 2,
                LocationString = "Test location",
                ImageUrls = [$"https://images.example.com/{version}.jpg"],
                MastodonLink = $"https://mastodon.example.com/@test/{version}",
                BlueskyLink = $"https://bsky.app/profile/test/post/{version}",
                ThreadsLink = $"https://www.threads.net/@test/post/{version}"
            };
        }

        private static SyndicationItem FeedItem(string id)
        {
            return new SyndicationItem(id, "Unrelated content", null, id, new DateTimeOffset(CreatedAt));
        }

        private static string Html(SyndicationItem item)
        {
            return Assert.IsType<TextSyndicationContent>(item.Content ?? item.Summary).Text;
        }

        private static void AssertUpdatedItem(SyndicationItem item, string version)
        {
            Assert.Equal(ReportId, item.Id);
            Assert.Equal("2 cars @ Test location", item.Title.Text);
            string html = Html(item);
            Assert.Contains($"https://images.example.com/{version}.jpg", html);
            Assert.Contains($"https://mastodon.example.com/@test/{version}", html);
            Assert.Contains($"https://bsky.app/profile/test/post/{version}", html);
            Assert.Contains($"https://www.threads.net/@test/post/{version}", html);
        }

        private static FeedProvider CreateProvider(MemoryFeedBlob rss, MemoryFeedBlob atom)
        {
            Mock<BlobContainerClient> container = new Mock<BlobContainerClient>(MockBehavior.Strict);
            container.Setup(c => c.GetBlobClient("rss.xml")).Returns(rss.Client.Object);
            container.Setup(c => c.GetBlobClient("atom.xml")).Returns(atom.Client.Object);
            ReportedItemsDatabase database = new ReportedItemsDatabase(NullLogger<ReportedItemsDatabase>.Instance,
                new Mock<Container>(MockBehavior.Strict).Object);
            return new FeedProvider(NullLogger<FeedProvider>.Instance, database, container.Object);
        }

        private sealed class MemoryFeedBlob
        {
            private BinaryData content;

            public Mock<BlobClient> Client { get; } = new Mock<BlobClient>(MockBehavior.Strict);
            public Exception? UploadFailure { get; set; }
            public int UploadAttempts { get; private set; }
            public string? ContentType { get; private set; }

            public MemoryFeedBlob(bool rss, IEnumerable<SyndicationItem> items)
            {
                SyndicationFeed feed = new SyndicationFeed("Reports", "Reported cars",
                                                    new Uri("https://seattle.carinbikelane.com"), "urn:test:feed", new DateTimeOffset(CreatedAt))
                {
                    Items = items
                };
                using MemoryStream stream = new MemoryStream();
                using (var writer = XmlWriter.Create(stream, new XmlWriterSettings() { Encoding = new UTF8Encoding(false) }))
                {
                    if (rss)
                    {
                        new Rss20FeedFormatter(feed).WriteTo(writer);
                    }
                    else
                    {
                        new Atom10FeedFormatter(feed).WriteTo(writer);
                    }
                }
                content = BinaryData.FromBytes(stream.ToArray());

                Client.Setup(c => c.DownloadContentAsync())
                    .Returns(() => Task.FromResult(Response.FromValue(
                        BlobsModelFactory.BlobDownloadResult(content), Mock.Of<Response>())));
                Client.Setup(c => c.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobHttpHeaders>(),
                        null, null, null, null, default, CancellationToken.None))
                    .Returns((Stream data, BlobHttpHeaders headers, IDictionary<string, string>? metadata,
                        BlobRequestConditions? conditions, IProgress<long>? progress, AccessTier? accessTier,
                        StorageTransferOptions transferOptions, CancellationToken cancellationToken) =>
                    {
                        UploadAttempts++;
                        if (UploadFailure != null)
                        {
                            return Task.FromException<Azure.Response<BlobContentInfo>>(UploadFailure);
                        }

                        content = BinaryData.FromStream(data);
                        ContentType = headers.ContentType;
                        return Task.FromResult(Response.FromValue(
                            BlobsModelFactory.BlobContentInfo(eTag: new ETag("test"),
                                lastModified: DateTimeOffset.UtcNow, contentHash: null, versionId: null,
                                encryptionKeySha256: null, encryptionScope: null, blobSequenceNumber: 0),
                            Mock.Of<Response>()));
                    });
            }

            public List<SyndicationItem> ReadItems()
            {
                using var stream = content.ToStream();
                using var reader = XmlReader.Create(stream);
                return SyndicationFeed.Load(reader).Items.ToList();
            }
        }
    }
}
