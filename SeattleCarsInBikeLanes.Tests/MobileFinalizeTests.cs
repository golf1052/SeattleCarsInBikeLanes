using System.Net;
using System.Security.Claims;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SeattleCarsInBikeLanes.Controllers;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Tests;

public class MobileFinalizeTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task LostResponseFreshPreparationAndExpiredCredentialsRecoverFirstAttribution()
    {
        Fixture f = new();
        f.Storage.FailAfterWrite = true;
        IActionResult uncertain = await f.Controller().FinalizeMobile(f.Request("first", attributed: true), f.Mastodon, default);
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(uncertain).StatusCode);
        f.BearerValid = false;
        UploadController restarted = f.Controller();
        OkObjectResult retried = Assert.IsType<OkObjectResult>(
            await restarted.FinalizeMobile(f.Request("different", attributed: false), f.Mastodon, default));
        SubmissionReceipt receipt = Assert.IsType<SubmissionReceipt>(retried.Value);
        Assert.Equal("did:plc:a", receipt.Attribution.BlueskyDid);
        Assert.Equal(Id, receipt.SubmissionId);
        Assert.Equal(1, f.Storage.Writes);
        Assert.Equal("did:plc:a", (await f.Storage.Service().GetForModerationAsync(Id)).Photos[0].Metadata.BlueskyUserDid);
    }

    [Fact]
    public async Task ExplicitAnonymousIgnoresValidAmbientCookieAndBearerAndStripsAllCredit()
    {
        Fixture f = new();
        MobileFinalizeRequest request = f.Request("first", attributed: false);
        request.Photos[0].BlueskySubmittedBy = "Submitted by B";
        request.Photos[0].MastodonSubmittedBy = "Submitted by B";
        request.Photos[0].MastodonAccessToken = "ambient-secret";
        await f.Controller().FinalizeMobile(request, f.Mastodon, default);
        FinalizedPhotoUploadMetadata photo = (await f.Storage.Service().GetForModerationAsync(Id)).Photos[0].Metadata;
        Assert.False(photo.Attribute);
        Assert.Null(photo.BlueskyUserDid);
        Assert.Null(photo.MastodonAccessToken);
        Assert.Null(photo.MastodonUsername);
        Assert.Equal("Submission", photo.BlueskySubmittedBy);
        Assert.Equal("Submission", photo.MastodonSubmittedBy);
    }

    [Fact]
    public async Task ValidCookieCannotValidateExpiredNativeBearer()
    {
        Fixture f = new() { BearerValid = false };
        UnauthorizedObjectResult result = Assert.IsType<UnauthorizedObjectResult>(
            await f.Controller().FinalizeMobile(f.Request("first", true), f.Mastodon, default));
        Assert.Equal(MobileUploadErrors.CredentialRejected, Assert.IsType<MobileUploadError>(result.Value).Code);
        Assert.Equal(0, f.Storage.Writes);
        Assert.Null(await f.Storage.Service().GetAsync(Id, "device"));
    }

    [Fact]
    public async Task MismatchedIdentityFailsBeforeStorageAndStorageOutageCannotFailOpen()
    {
        Fixture f = new();
        MobileFinalizeRequest request = f.Request("first", true) with { Attribution = new ReportAttribution("did:plc:b") };
        Assert.IsType<ConflictObjectResult>(await f.Controller().FinalizeMobile(request, f.Mastodon, default));
        Assert.Equal(0, f.Storage.Writes);
        f.Storage.FailReads = true;
        StatusCodeResult result = Assert.IsType<StatusCodeResult>(
            await f.Controller().FinalizeMobile(f.Request("first", false), f.Mastodon, default));
        Assert.Equal(503, result.StatusCode);
        Assert.Equal(0, f.Storage.Writes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    [InlineData(HttpStatusCode.TooManyRequests, 503)]
    public async Task MastodonRejectsCredentialsSeparatelyFromProviderUnavailability(HttpStatusCode status, int expected)
    {
        Fixture f = new();
        f.MastodonResponse = status;
        MobileFinalizeRequest request = f.Request("first", false) with
        { Attribution = new ReportAttribution(MastodonServer: "https://example.test", MastodonAccountId: "a") };
        request.Photos[0].MastodonAccessToken = "retained-a";
        ObjectResult result = Assert.IsAssignableFrom<ObjectResult>(
            await f.Controller().FinalizeMobile(request, f.Mastodon, default));
        Assert.Equal(expected, result.StatusCode);
        Assert.Equal(0, f.Storage.Writes);
    }

    private sealed class Fixture
    {
        public SubmissionClaimProviderTests.Storage Storage = new();
        public bool BearerValid = true;
        public HttpStatusCode MastodonResponse = HttpStatusCode.Unauthorized;
        public MastodonCredentialVerifier Mastodon => new(new HttpClient(new Handler(() => new HttpResponseMessage(MastodonResponse))));

        public MobileFinalizeRequest Request(string attempt, bool attributed)
        {
            string photoId = $"{attempt}photo";
            Mock<BlobClient> image = new();
            image.Setup(b => b.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobProperties(contentLength: 5, eTag: new ETag("photo")),
                    Mock.Of<Response>()));
            image.Setup(b => b.DownloadContentAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobDownloadResult(BinaryData.FromBytes([255, 216, 1, 255, 217])),
                    Mock.Of<Response>()));
            Mock<BlobClient> metadata = new();
            metadata.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobDownloadResult(BinaryData.FromObjectAsJson(
                    new InitialPhotoUploadMetadata(photoId, attempt, 0, []))), Mock.Of<Response>()));
            Storage.Container.Setup(c => c.GetBlobClient($"{UploadController.InitialUploadPrefix}{photoId}.jpeg")).Returns(image.Object);
            Storage.Container.Setup(c => c.GetBlobClient($"{UploadController.InitialUploadPrefix}{photoId}.json")).Returns(metadata.Object);
            return new MobileFinalizeRequest([new FinalizedPhotoUpload
            {
                PhotoId = photoId, SubmissionId = attempt, PhotoNumber = 0, NumberOfCars = 1,
                PhotoDateTime = DateTime.UtcNow, PhotoLatitude = "47.6062", PhotoLongitude = "-122.3321",
                PhotoCrossStreet = "Pike St", Attribute = attributed, BlueskySubmittedBy = "Submitted by A"
            }], new ReportAttribution(attributed ? "did:plc:a" : null));
        }

        public UploadController Controller()
        {
            MemoryCache cache = new(new MemoryCacheOptions());
            cache.Set("DeviceBlocklist", new HashSet<string>());
            Mock<SecretClient> secrets = new();
            secrets.Setup(s => s.GetSecret(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Response.FromValue(SecretModelFactory.KeyVaultSecret(new SecretProperties("test"), "dummy"), Mock.Of<Response>()));
            SlackbotProvider slack = new(NullLogger<SlackbotProvider>.Instance,
                new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK))), secrets.Object);
            UploadController controller = new(NullLogger<UploadController>.Instance, null!, null!, null!,
                Storage.Container.Object, null!, slack,
                new DeviceBlocklistProvider(NullLogger<DeviceBlocklistProvider>.Instance, Storage.Container.Object, cache),
                Storage.Service(), null!);
            Mock<IAuthenticationService> authentication = new();
            authentication.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>()))
                .Returns<HttpContext, string>((_, scheme) => Task.FromResult(
                    scheme == BlueskyAuthDefaults.CookieScheme || BearerValid
                        ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                            new Claim(BlueskyAuthDefaults.DidClaim, "did:plc:a"),
                            new Claim(BlueskyAuthDefaults.HandleClaim, "a.bsky.social")], scheme)), scheme))
                        : AuthenticateResult.Fail("expired")));
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddSingleton(authentication.Object).BuildServiceProvider()
                }
            };
            controller.Request.Headers[UploadController.ReportIdHeader] = Id;
            controller.Request.Headers[UploadController.DeviceIdHeader] = "device";
            controller.Request.Headers.Authorization = "Bearer queued-a";
            return controller;
        }
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    [Fact]
    public async Task OversizedChunkedIdentityResponseIsBoundedAndIsNotCredentialRejection()
    {
        using HttpClient client = new(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[65 * 1024]))
        }));
        MastodonCredentialVerifier verifier = new(client);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            verifier.VerifyAsync("https://example.test", "dummy", default));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
