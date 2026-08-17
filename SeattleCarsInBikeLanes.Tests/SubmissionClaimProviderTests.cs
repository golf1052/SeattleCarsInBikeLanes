using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Tests
{
    public class SubmissionClaimProviderTests
    {
        private const string ReportId = "0123456789abcdef0123456789abcdef";

        [Theory]
        [InlineData(ReportId, true)]
        [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
        [InlineData("../blockeddevices.json", false)]
        [InlineData("0123456789abcdef", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ValidatesReportIds(string? reportId, bool expected)
        {
            Assert.Equal(expected, SubmissionClaimProvider.IsValidReportId(reportId));
        }

        [Fact]
        public async Task ClaimsAnUnseenReport()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            BlobUploadOptions? uploadOptions = null;
            blobClient.Setup(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .Callback<BinaryData, BlobUploadOptions, CancellationToken>((_, options, _) =>
                    uploadOptions = options)
                .ReturnsAsync((Response<BlobContentInfo>)null!);

            SubmissionClaimResult result = await CreateProvider(blobClient).TryClaimAsync(ReportId, "device");

            Assert.Equal(SubmissionClaimResult.Claimed, result);
            Assert.Equal(ETag.All, uploadOptions?.Conditions?.IfNoneMatch);
        }

        [Fact]
        public async Task RecognizesACompletedReport()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            SetupExistingClaim(blobClient,
                $$"""{"Status":"Completed","ClaimedAt":"{{DateTimeOffset.UtcNow:O}}"}""");

            SubmissionClaimResult result = await CreateProvider(blobClient).TryClaimAsync(ReportId, "device");

            Assert.Equal(SubmissionClaimResult.AlreadyCompleted, result);
        }

        [Fact]
        public async Task ResolvesAnExistingClaimWhenConditionalCreateReturnsPreconditionFailed()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            SetupExistingClaim(blobClient,
                $$"""{"Status":"Completed","ClaimedAt":"{{DateTimeOffset.UtcNow:O}}"}""",
                StatusCodes.Status412PreconditionFailed);

            SubmissionClaimResult result = await CreateProvider(blobClient).TryClaimAsync(ReportId, "device");

            Assert.Equal(SubmissionClaimResult.AlreadyCompleted, result);
        }

        [Fact]
        public async Task DefersToAFreshInProgressReport()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            SetupExistingClaim(blobClient,
                $$"""{"Status":"InProgress","ClaimedAt":"{{DateTimeOffset.UtcNow:O}}"}""");

            SubmissionClaimResult result = await CreateProvider(blobClient).TryClaimAsync(ReportId, "device");

            Assert.Equal(SubmissionClaimResult.InFlight, result);
        }

        [Fact]
        public async Task TakesOverAStaleClaimConditionally()
        {
            ETag existingETag = new ETag("\"existing\"");
            Mock<BlobClient> blobClient = CreateBlobClient();
            blobClient.SetupSequence(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(409, "BlobAlreadyExists"))
                .ReturnsAsync((Response<BlobContentInfo>)null!);
            SetupDownload(blobClient,
                $$"""{"Status":"InProgress","ClaimedAt":"{{DateTimeOffset.UtcNow.AddMinutes(-10):O}}"}""",
                existingETag);

            SubmissionClaimResult result = await CreateProvider(blobClient).TryClaimAsync(ReportId, "device");

            Assert.Equal(SubmissionClaimResult.Claimed, result);
            blobClient.Verify(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.Is<BlobUploadOptions>(options => options.Conditions!.IfMatch == existingETag),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task LosesAStaleTakeoverRaceWithoutFinalizing()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            blobClient.SetupSequence(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(409, "BlobAlreadyExists"))
                .ThrowsAsync(new RequestFailedException(412, "ConditionNotMet"));
            SetupDownload(blobClient,
                $$"""{"Status":"InProgress","ClaimedAt":"{{DateTimeOffset.UtcNow.AddMinutes(-10):O}}"}""");

            SubmissionClaimResult result = await CreateProvider(blobClient).TryClaimAsync(ReportId, "device");

            Assert.Equal(SubmissionClaimResult.InFlight, result);
        }

        [Fact]
        public async Task FailsOpenWhenClaimStorageIsUnavailable()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            blobClient.Setup(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(503, "Unavailable"));

            SubmissionClaimResult result = await CreateProvider(blobClient).TryClaimAsync(ReportId, "device");

            Assert.Equal(SubmissionClaimResult.Claimed, result);
        }

        [Fact]
        public async Task FailsOpenWhenClaimCreationThrowsANonStorageException()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            blobClient.Setup(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Credential refresh failed"));

            SubmissionClaimResult result = await CreateProvider(blobClient).TryClaimAsync(ReportId, "device");

            Assert.Equal(SubmissionClaimResult.Claimed, result);
        }

        [Fact]
        public async Task CompletesAClaimWithAnOverwrite()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            blobClient.Setup(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    true,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response<BlobContentInfo>)null!);

            await CreateProvider(blobClient).CompleteAsync(ReportId, "device");

            blobClient.Verify(c => c.UploadAsync(
                    It.Is<BinaryData>(data => data.ToString().Contains("\"Status\":\"Completed\"")),
                    true,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task CompletionDoesNotFailTheFinalizedReport()
        {
            Mock<BlobClient> blobClient = CreateBlobClient();
            blobClient.Setup(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    true,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Credential refresh failed"));

            await CreateProvider(blobClient).CompleteAsync(ReportId, "device");
        }

        private static SubmissionClaimProvider CreateProvider(Mock<BlobClient> blobClient)
        {
            Mock<BlobContainerClient> containerClient = new Mock<BlobContainerClient>();
            containerClient.Setup(c => c.GetBlobClient($"{SubmissionClaimProvider.BlobPrefix}{ReportId}.json"))
                .Returns(blobClient.Object);

            return new SubmissionClaimProvider(NullLogger<SubmissionClaimProvider>.Instance,
                containerClient.Object);
        }

        private static Mock<BlobClient> CreateBlobClient() => new Mock<BlobClient>();

        private static void SetupExistingClaim(Mock<BlobClient> blobClient,
            string json,
            int status = StatusCodes.Status409Conflict)
        {
            blobClient.Setup(c => c.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(status, "Claim already exists"));
            SetupDownload(blobClient, json);
        }

        private static void SetupDownload(Mock<BlobClient> blobClient,
            string json,
            ETag eTag = default)
        {
            BlobDownloadDetails details = BlobsModelFactory.BlobDownloadDetails(eTag: eTag);
            BlobDownloadResult download =
                BlobsModelFactory.BlobDownloadResult(BinaryData.FromString(json), details);
            blobClient.Setup(c => c.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(download, Mock.Of<Response>()));
        }
    }
}
