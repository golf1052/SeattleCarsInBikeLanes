using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Maps.Search;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using golf1052.atproto.net;
using golf1052.atproto.net.Models.AtProto.Repo;
using golf1052.atproto.net.Models.Bsky.Feed;
using golf1052.atproto.net.Models.Bsky.Richtext;
using golf1052.Mastodon;
using golf1052.Mastodon.Models.Statuses;
using golf1052.Mastodon.Models.Statuses.Media;
using golf1052.ThreadsAPI;
using Imgur.API.Authentication;
using Imgur.API.Endpoints;
using Imgur.API.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Contrib.HttpClient;
using SeattleCarsInBikeLanes.Controllers;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Providers;
using static SeattleCarsInBikeLanes.Controllers.AdminPageController;

namespace SeattleCarsInBikeLanes.Tests
{
    public class AdminPageControllerTests
    {
        private AdminPageController? controller;
        private ILogger<AdminPageController>? logger;
        private Mock<HelperMethods>? mockHelperMethods;
        private Mock<BlobContainerClient>? mockBlobContainerClient;
        private Mock<SecretClient>? mockSecretClient;
        private Mock<IApiClient>? mockImgurApiClient;
        private Mock<IImageEndpoint>? mockImageEndpoint;
        private Mock<Container>? mockContainer;
        private Mock<ReportedItemsDatabase>? mockReportedItemsDatabase;
        private Mock<HttpMessageHandler>? mockHttpMessageHandler;
        private Mock<TokenCredential>? mockTokenCredential;
        private Mock<HttpMessageHandler>? mockMapsSearchClientHttpMessageHandler;
        private MapsSearchClient? mapsSearchClient;
        private Mock<IWebHostEnvironment>? mockWebHostEnvironment;
        private Mock<MastodonOAuthMappingDatabase>? mockMastodonOAuthMappingDatabase;
        private Mock<MastodonClientProvider>? mockMastodonClientProvider;
        private Mock<FeedProvider>? mockFeedProvider;
        private Mock<BlueskyClientProvider>? mockBlueskyClientProvider;
        private Mock<MastodonClient>? mockMastodonClient;
        private Mock<AtProtoClient>? mockBlueskyClient;
        private Mock<ThreadsClient>? mockThreadsClient;

        public AdminPageControllerTests()
        {
            logger = NullLogger<AdminPageController>.Instance;
            mockHttpMessageHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            mockHelperMethods = new Mock<HelperMethods>();
            mockBlobContainerClient = new Mock<BlobContainerClient>();
            mockSecretClient = new Mock<SecretClient>();
            mockImgurApiClient = new Mock<IApiClient>();
            mockImgurApiClient.Setup(m => m.ClientId).Returns("1234");
            mockImgurApiClient.Setup(m => m.BaseAddress).Returns("https://api.imgur.com/3/");
            mockImageEndpoint = new Mock<IImageEndpoint>();
            mockContainer = new Mock<Container>();
            mockReportedItemsDatabase = new Mock<ReportedItemsDatabase>(NullLogger<ReportedItemsDatabase>.Instance,
                mockContainer.Object);
            mockTokenCredential = new Mock<TokenCredential>();
            AccessToken token = new AccessToken("access-token", DateTimeOffset.Now.AddYears(1));
            mockTokenCredential.Setup(m => m.GetToken(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
                .Returns(token);
            mockTokenCredential.Setup(m => m.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()).Result)
                .Returns(token);
            mockMapsSearchClientHttpMessageHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            MapsSearchClientOptions options = new MapsSearchClientOptions()
            {
                Transport = new HttpClientTransport(mockMapsSearchClientHttpMessageHandler.CreateClient())
            };
            mapsSearchClient = new MapsSearchClient(mockTokenCredential.Object, "client-id", options);
            mockWebHostEnvironment = new Mock<IWebHostEnvironment>();
            mockMastodonOAuthMappingDatabase = new Mock<MastodonOAuthMappingDatabase>(NullLogger<MastodonOAuthMappingDatabase>.Instance,
                mockContainer.Object);
            mockMastodonClientProvider = new Mock<MastodonClientProvider>(NullLogger<MastodonClientProvider>.Instance,
                mockWebHostEnvironment.Object,
                mockMastodonOAuthMappingDatabase.Object,
                mockSecretClient.Object,
                NullLogger<MastodonClient>.Instance,
                mockHttpMessageHandler.CreateClient());
            mockFeedProvider = new Mock<FeedProvider>(NullLogger<FeedProvider>.Instance,
                mockReportedItemsDatabase.Object,
                mockBlobContainerClient.Object);
            mockBlueskyClientProvider = new Mock<BlueskyClientProvider>(NullLogger<BlueskyClientProvider>.Instance,
                mockSecretClient.Object,
                mockHttpMessageHandler.CreateClient());
            mockMastodonClient = new Mock<MastodonClient>("https://mastodon.social",
                mockHttpMessageHandler.CreateClient());
            mockBlueskyClient = new Mock<AtProtoClient>(mockHttpMessageHandler.CreateClient(), null!, null!);
            mockThreadsClient = new Mock<ThreadsClient>("clientId", "clientSecret", mockHttpMessageHandler.CreateClient());

            mockSecretClient.Setup(m => m.GetSecret(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Azure.Response.FromValue(SecretModelFactory.KeyVaultSecret(new SecretProperties("test"), "test"), Mock.Of<Azure.Response>()));

            controller = new AdminPageController(logger,
                mockHelperMethods.Object,
                mockBlobContainerClient.Object,
                mockSecretClient.Object,
                mockImageEndpoint.Object,
                mockReportedItemsDatabase.Object,
                mockHttpMessageHandler.CreateClient(),
                mapsSearchClient,
                mockMastodonClientProvider.Object,
                mockFeedProvider.Object,
                mockBlueskyClientProvider.Object,
                mockThreadsClient.Object);
        }

        [Fact]
        public async Task PostTweet_SubmittedByTwitter()
        {
            PostTweetRequest request = new PostTweetRequest()
            {
                TweetBody = "1 car\nDate: 4/20/2023\nTime: 4:20 PM\nLocation: 9th Ave N & Mercer\nSubmitted by @golf1052",
                TweetImages = "https://golf1052.com/images/gv3-500.png",
                TweetLink = "https://golf1052.com"
            };

            mockMastodonClientProvider!.Setup(m => m.GetServerClient())
                .Returns(mockMastodonClient!.Object);
            mockBlueskyClientProvider!.Setup(m => m.GetClient().Result)
                .Returns(mockBlueskyClient!.Object);
            mockMapsSearchClientHttpMessageHandler.SetupAnyRequest()
                .ReturnsResponse(File.ReadAllText("TestFiles/SearchAddressResponse.json"), "application/json");
            mockHelperMethods!.Setup(m => m.DownloadImage(It.IsAny<string>(), It.IsAny<HttpClient>()).Result)
                .Returns(new MemoryStream());
            mockImageEndpoint!.Setup(m => m.UploadImageAsync(It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IProgress<int>>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()).Result)
                .Returns(new Image()
                {
                    Link = "https://golf1052.com/images/gv3-500.png"
                });
            MastodonAttachment mastodonAttachment = new MastodonAttachment()
            {
                Id = "test-id"
            };
            mockMastodonClient.Setup(m => m.UploadMedia(It.IsAny<Stream>()).Result)
                .Returns(mastodonAttachment);
            mockMastodonClient.Setup(m => m.GetAttachment(It.IsAny<string>()).Result)
                .Returns(mastodonAttachment);
            mockMastodonClient.Setup(m => m.PublishStatus(It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string>(),
                It.IsAny<string>()).Result)
                .Returns(new MastodonStatus()
                {
                    Url = "https://example.com/status/test-id"
                });
            mockBlueskyClient.Setup(m => m.UploadBlob(It.IsAny<UploadBlobRequest>()).Result)
                .Returns(new UploadBlobResponse()
                {
                    Blob = new AtProtoBlob()
                    {
                        Type = "blob",
                        Ref = new AtProtoBlobRef()
                        {
                            Link = "1234"
                        },
                        MimeType = "image/jpeg",
                        Size = 0
                    }
                });
            mockBlueskyClient.Setup(m => m.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()).Result)
                .Returns((CreateRecordRequest<BskyPost> request) => {
                    Assert.NotNull(request.Record.Facets);
                    Assert.Single(request.Record.Facets);
                    return new CreateRecordResponse()
                    {
                        Cid = "1234",
                        Uri = "at://did:plc:1234/app.bsky.feed.post/1234"
                    };
                });
            mockHelperMethods.Setup(m => m.GetBlueskyPostUrl(It.IsAny<string>()))
                .Returns("https://example.com/status/test-id");
            mockReportedItemsDatabase!.Setup(m => m.AddReportedItem(It.IsAny<ReportedItem>()).Result)
                .Returns(true);
            mockFeedProvider!.Setup(m => m.AddReportedItemToFeed(It.IsAny<ReportedItem>()));

            var result = await controller!.PostTweet(request);
        }

        [Fact]
        public async Task PostTweet_SubmittedByMastodon()
        {
            PostTweetRequest request = new PostTweetRequest()
            {
                TweetBody = "1 car\nDate: 4/20/2023\nTime: 4:20 PM\nLocation: 9th Ave N & Mercer\nSubmitted by https://mastodon.social/@golf1052",
                TweetImages = "https://golf1052.com/images/gv3-500.png",
                TweetLink = "https://golf1052.com"
            };

            mockMastodonClientProvider!.Setup(m => m.GetServerClient())
                .Returns(mockMastodonClient!.Object);
            mockBlueskyClientProvider!.Setup(m => m.GetClient().Result)
                .Returns(mockBlueskyClient!.Object);
            mockMapsSearchClientHttpMessageHandler.SetupAnyRequest()
                .ReturnsResponse(File.ReadAllText("TestFiles/SearchAddressResponse.json"), "application/json");
            mockHelperMethods!.Setup(m => m.DownloadImage(It.IsAny<string>(), It.IsAny<HttpClient>()).Result)
                .Returns(new MemoryStream());
            mockImageEndpoint!.Setup(m => m.UploadImageAsync(It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IProgress<int>>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()).Result)
                .Returns(new Image()
                {
                    Link = "https://golf1052.com/images/gv3-500.png"
                });
            MastodonAttachment mastodonAttachment = new MastodonAttachment()
            {
                Id = "test-id"
            };
            mockMastodonClient.Setup(m => m.UploadMedia(It.IsAny<Stream>()).Result)
                .Returns(mastodonAttachment);
            mockMastodonClient.Setup(m => m.GetAttachment(It.IsAny<string>()).Result)
                .Returns(mastodonAttachment);
            mockMastodonClient.Setup(m => m.PublishStatus(It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string>(),
                It.IsAny<string>()).Result)
                .Returns(new MastodonStatus()
                {
                    Url = "https://example.com/status/test-id"
                });
            mockBlueskyClient.Setup(m => m.UploadBlob(It.IsAny<UploadBlobRequest>()).Result)
                .Returns(new UploadBlobResponse()
                {
                    Blob = new AtProtoBlob()
                    {
                        Type = "blob",
                        Ref = new AtProtoBlobRef()
                        {
                            Link = "1234"
                        },
                        MimeType = "image/jpeg",
                        Size = 0
                    }
                });
            mockBlueskyClient.Setup(m => m.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()).Result)
                .Returns((CreateRecordRequest<BskyPost> request) => {
                    Assert.NotNull(request.Record.Facets);
                    Assert.Single(request.Record.Facets);
                    return new CreateRecordResponse()
                    {
                        Cid = "1234",
                        Uri = "at://did:plc:1234/app.bsky.feed.post/1234"
                    };
                });
            mockHelperMethods.Setup(m => m.GetBlueskyPostUrl(It.IsAny<string>()))
                .Returns("https://example.com/status/test-id");
            mockReportedItemsDatabase!.Setup(m => m.AddReportedItem(It.IsAny<ReportedItem>()).Result)
                .Returns(true);
            mockFeedProvider!.Setup(m => m.AddReportedItemToFeed(It.IsAny<ReportedItem>()));

            var result = await controller!.PostTweet(request);
        }

        [Fact]
        public async Task PostTweet_WithBskyLink()
        {
            PostTweetRequest request = new PostTweetRequest()
            {
                TweetBody = "1 car\nDate: 4/20/2023\nTime: 4:20 PM\nLocation: 9th Ave N & Mercer\nhttps://bsky.app/profile/golf1052.com/post/3k366ccs2hi2h",
                TweetImages = "https://golf1052.com/images/gv3-500.png",
                TweetLink = "https://golf1052.com"
            };

            mockMastodonClientProvider!.Setup(m => m.GetServerClient())
                .Returns(mockMastodonClient!.Object);
            mockBlueskyClientProvider!.Setup(m => m.GetClient().Result)
                .Returns(mockBlueskyClient!.Object);
            mockMapsSearchClientHttpMessageHandler.SetupAnyRequest()
                .ReturnsResponse(File.ReadAllText("TestFiles/SearchAddressResponse.json"), "application/json");
            mockHelperMethods!.Setup(m => m.DownloadImage(It.IsAny<string>(), It.IsAny<HttpClient>()).Result)
                .Returns(new MemoryStream());
            mockImageEndpoint!.Setup(m => m.UploadImageAsync(It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IProgress<int>>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()).Result)
                .Returns(new Image()
                {
                    Link = "https://golf1052.com/images/gv3-500.png"
                });
            MastodonAttachment mastodonAttachment = new MastodonAttachment()
            {
                Id = "test-id"
            };
            mockMastodonClient.Setup(m => m.UploadMedia(It.IsAny<Stream>()).Result)
                .Returns(mastodonAttachment);
            mockMastodonClient.Setup(m => m.GetAttachment(It.IsAny<string>()).Result)
                .Returns(mastodonAttachment);
            mockMastodonClient.Setup(m => m.PublishStatus(It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string>(),
                It.IsAny<string>()).Result)
                .Returns(new MastodonStatus()
                {
                    Url = "https://example.com/status/test-id"
                });
            mockBlueskyClient.Setup(m => m.UploadBlob(It.IsAny<UploadBlobRequest>()).Result)
                .Returns(new UploadBlobResponse()
                {
                    Blob = new AtProtoBlob()
                    {
                        Type = "blob",
                        Ref = new AtProtoBlobRef()
                        {
                            Link = "1234"
                        },
                        MimeType = "image/jpeg",
                        Size = 0
                    }
                });
            mockBlueskyClient.Setup(m => m.CreateRecord(It.IsAny<CreateRecordRequest<BskyPost>>()).Result)
                .Returns((CreateRecordRequest<BskyPost> request) => {
                    Assert.NotNull(request.Record.Facets);
                    Assert.Single(request.Record.Facets);
                    Assert.Single(request.Record.Facets[0].Features);
                    BskyLink? bskyLink = request.Record.Facets[0].Features[0] as BskyLink;
                    Assert.NotNull(bskyLink);
                    Assert.Equal("https://bsky.app/profile/golf1052.com/post/3k366ccs2hi2h", bskyLink.Uri);
                    return new CreateRecordResponse()
                    {
                        Cid = "1234",
                        Uri = "at://did:plc:1234/app.bsky.feed.post/1234"
                    };
                });
            mockHelperMethods.Setup(m => m.GetBlueskyPostUrl(It.IsAny<string>()))
                .Returns("https://example.com/status/test-id");
            mockReportedItemsDatabase!.Setup(m => m.AddReportedItem(It.IsAny<ReportedItem>()).Result)
                .Returns(true);
            mockFeedProvider!.Setup(m => m.AddReportedItemToFeed(It.IsAny<ReportedItem>()));

            var result = await controller!.PostTweet(request);
        }
    }
}
