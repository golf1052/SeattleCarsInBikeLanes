using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class UploadServiceTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task PreparationSendsTheDurableReportAndDeviceIdsWithOrderedJpegs(int count)
    {
        List<InitialPhotoUpload> prepared = Enumerable.Range(0, count).Select(i =>
            new InitialPhotoUpload { PhotoId = $"photo-{i}", SubmissionId = "attempt", PhotoNumber = i }).ToList();
        TestHttpHandler handler = new();
        handler.Response = async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/Upload/Initial", request.RequestUri!.AbsolutePath);
            Assert.Equal(Id, Assert.Single(request.Headers.GetValues("X-Report-Id")));
            Assert.Equal("device", Assert.Single(request.Headers.GetValues("X-Device-Id")));
            Assert.Equal("Anonymous", request.Headers.Authorization?.Scheme);
            HttpContent[] parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal(count, parts.Length);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal("files", parts[i].Headers.ContentDisposition?.Name?.Trim('"'));
                Assert.Equal($"photo{i}.jpg", parts[i].Headers.ContentDisposition?.FileName?.Trim('"'));
                Assert.Equal("image/jpeg", parts[i].Headers.ContentType?.MediaType);
                Assert.Equal(new byte[] { (byte)i, 1, 2 }, await parts[i].ReadAsByteArrayAsync(token));
            }
            return AuthServiceTests.Json(prepared);
        };
        UploadPreparation result = await Create(new HttpClient(handler)).PrepareAsync(
            Enumerable.Range(0, count).Select(i => new UploadPhoto($"local-{i}", [(byte)i, 1, 2])).ToArray(), Id);
        Assert.Equal(prepared.Select(p => p.PhotoId), result.Photos.Select(p => p.PhotoId));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("unordered")]
    [InlineData("mixed-submissions")]
    [InlineData("duplicate")]
    [InlineData("missing-id")]
    [InlineData("null-photo")]
    public async Task PreparationRejectsAnInvalidPhotoSet(string failure)
    {
        List<InitialPhotoUpload> prepared =
        [
            new() { PhotoId = "one", SubmissionId = "attempt", PhotoNumber = 0 },
            new() { PhotoId = "two", SubmissionId = "attempt", PhotoNumber = 1 }
        ];
        switch (failure)
        {
            case "incomplete": prepared.RemoveAt(1); break;
            case "unordered": prepared[1].PhotoNumber = 0; break;
            case "mixed-submissions": prepared[1].SubmissionId = "another"; break;
            case "duplicate": prepared[1].PhotoId = "one"; break;
            case "missing-id": prepared[0].SubmissionId = ""; break;
        }
        TestHttpHandler handler = new()
        {
            Response = (_, _) => Task.FromResult(failure == "null-photo"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[null,null]") }
                : AuthServiceTests.Json(prepared))
        };
        await Assert.ThrowsAsync<UploadException>(() =>
            Create(new HttpClient(handler)).PrepareAsync([new("one", [1]), new("two", [2])], Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalizationUsesOnlyExplicitQueuedIdentity(bool anonymous)
    {
        TestHttpHandler handler = new();
        AccountSession credentials = new(new AccountCredential("did:plc:a", "a.bsky.social", "token-a"));
        ReportAttribution intent = anonymous ? new ReportAttribution() : credentials.Attribution;
        SubmissionReceipt receipt = new(Id, Id, DateTimeOffset.UtcNow, intent);
        handler.Response = async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/Upload/Finalize", request.RequestUri!.AbsolutePath);
            Assert.Equal(Id, Assert.Single(request.Headers.GetValues("X-Report-Id")));
            Assert.Equal("device", Assert.Single(request.Headers.GetValues("X-Device-Id")));
            Assert.Equal(anonymous ? "Anonymous" : "Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(anonymous ? null : "token-a", request.Headers.Authorization?.Parameter);
            FinalizeReportRequest body = (await request.Content!.ReadFromJsonAsync<FinalizeReportRequest>(token))!;
            Assert.Equal(intent, body.Attribution);
            Assert.Equal(anonymous ? "Submission" : "Submitted by a.bsky.social", body.Photos[0].BlueskySubmittedBy);
            Assert.DoesNotContain("token-b", await request.Content!.ReadAsStringAsync(token));
            return AuthServiceTests.Json(receipt);
        };
        HttpClient client = new(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "token-b");
        UploadService service = Create(client);
        SubmissionReceipt result = await service.FinalizeAsync(new UploadPreparation([
            new InitialPhotoUpload { PhotoId = "initial", SubmissionId = "attempt", PhotoNumber = 0 }]),
            new ReportDraft { Attribute = true }, new QueuedAttribution(intent), credentials, Id);
        Assert.Equal(receipt, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizationIncludesTheQueuedMastodonAccountWithOrWithoutBluesky(bool withBluesky)
    {
        AccountSession credentials = new(
            withBluesky ? new AccountCredential("did:plc:a", "a.bsky.social", "token-a") : null,
            new AccountCredential("42", "rider", "mastodon-token", "https://example.social"));
        TestHttpHandler handler = new();
        handler.Response = async (request, token) =>
        {
            Assert.Equal(withBluesky ? "Bearer" : "Anonymous", request.Headers.Authorization?.Scheme);
            FinalizeReportRequest body = (await request.Content!.ReadFromJsonAsync<FinalizeReportRequest>(token))!;
            Assert.Equal(credentials.Attribution, body.Attribution);
            Assert.Equal("42", body.Attribution.MastodonAccountId);
            Assert.All(body.Photos, photo => Assert.Equal("mastodon-token", photo.MastodonAccessToken));
            return AuthServiceTests.Json(new SubmissionReceipt(Id, Id, DateTimeOffset.UtcNow, body.Attribution));
        };
        await Create(new HttpClient(handler)).FinalizeAsync(new UploadPreparation([
            new InitialPhotoUpload { PhotoId = "one", SubmissionId = "attempt", PhotoNumber = 0 },
            new InitialPhotoUpload { PhotoId = "two", SubmissionId = "attempt", PhotoNumber = 1 }]),
            new ReportDraft { Attribute = true }, new QueuedAttribution(credentials.Attribution), credentials, Id);
    }

    [Fact]
    public async Task ReceiptLookupUsesTheSameDeviceWithoutActiveAccountCredentials()
    {
        SubmissionReceipt receipt = new(Id, Id, DateTimeOffset.UtcNow, new ReportAttribution());
        TestHttpHandler handler = new()
        {
            Response = (request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal($"/api/Upload/Reports/{Id}", request.RequestUri!.AbsolutePath);
                Assert.Equal("device", Assert.Single(request.Headers.GetValues("X-Device-Id")));
                Assert.Equal("Anonymous", request.Headers.Authorization?.Scheme);
                return Task.FromResult(AuthServiceTests.Json(receipt));
            }
        };
        Assert.Equal(receipt, await Create(new HttpClient(handler)).GetReceiptAsync(Id));
    }

    [Fact]
    public async Task StatusOutageIsNotAbsenceAndOnlyTypedRejectionAuthorizesFallback()
    {
        TestHttpHandler handler = new();
        UploadService service = Create(new HttpClient(handler));
        handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<UploadException>(() => service.GetReceiptAsync(Id));
        handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await service.GetReceiptAsync(Id));
        handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<UploadException>(() => service.GetReceiptAsync(Id));
        handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = JsonContent.Create(new UploadError(UploadErrors.CredentialRejected, "expired")) });
        await Assert.ThrowsAsync<QueuedCredentialRejectedException>(() => service.GetReceiptAsync(Id));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, UploadErrors.ReportInProgress)]
    [InlineData(HttpStatusCode.Conflict, UploadErrors.IdentityMismatch)]
    [InlineData(HttpStatusCode.Gone, UploadErrors.PreparationExpired)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UploadErrors.ProviderUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized, "unknown")]
    public async Task StructuredErrorsPreserveTheMessageCodeAndRetryDelay(HttpStatusCode status, string code)
    {
        TestHttpHandler handler = new()
        {
            Response = (_, _) =>
            {
                HttpResponseMessage response = new(status)
                {
                    Content = JsonContent.Create(new UploadError(code, "Please retry shortly."))
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
                return Task.FromResult(response);
            }
        };
        UploadException error = await Assert.ThrowsAsync<UploadException>(() =>
            Create(new HttpClient(handler)).GetReceiptAsync(Id));
        Assert.Equal("Please retry shortly.", error.Message);
        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(90), error.RetryAfter);
    }

    [Fact]
    public async Task DeviceBlockingPreservesThePlainTextExplanation()
    {
        TestHttpHandler handler = new()
        {
            Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("This device can't submit reports.")
            })
        };
        UploadException error = await Assert.ThrowsAsync<UploadException>(() =>
            Create(new HttpClient(handler)).PrepareAsync([new("photo", [1, 2, 3])], Id));
        Assert.True(error.IsBlocked);
        Assert.Null(error.Code);
        Assert.Equal("This device can't submit reports.", error.Message);
    }

    private static UploadService Create(HttpClient client) =>
        new(client, new Device(), new PassthroughImageResizer(), NullLogger<UploadService>.Instance);
    private sealed class Device : IDeviceIdentityService
    {
        public Task<string> GetDeviceIdAsync() => Task.FromResult("device");
    }
}
