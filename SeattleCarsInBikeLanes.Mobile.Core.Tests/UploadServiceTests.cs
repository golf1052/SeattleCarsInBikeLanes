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
            Assert.Equal(anonymous ? "Anonymous" : "Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(anonymous ? null : "token-a", request.Headers.Authorization?.Parameter);
            MobileFinalizeRequest body = (await request.Content!.ReadFromJsonAsync<MobileFinalizeRequest>(token))!;
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
            new ReportDraft { Attribute = true }, new QueuedAttribution(intent), anonymous ? null : credentials, Id);
        Assert.Equal(receipt, result);
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
        { Content = JsonContent.Create(new MobileUploadError(MobileUploadErrors.CredentialRejected, "expired")) });
        await Assert.ThrowsAsync<QueuedCredentialRejectedException>(() => service.GetReceiptAsync(Id));
    }

    private static UploadService Create(HttpClient client) =>
        new(client, new Device(), new PassthroughImageResizer(), NullLogger<UploadService>.Instance);
    private sealed class Device : IDeviceIdentityService
    {
        public Task<string> GetDeviceIdAsync() => Task.FromResult("device");
    }
}
