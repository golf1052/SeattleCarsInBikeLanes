using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SeattleCarsInBikeLanes.Controllers;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Providers;
using Cosmos = Microsoft.Azure.Cosmos;

namespace SeattleCarsInBikeLanes.Tests
{
    public partial class UploadFinalizationTests
    {
        private const string BlockedInstallation = "installation-1";

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task BlockedDeviceCannotPrepareOrAcceptANewReport(bool initial)
        {
            using Fixture fixture = new Fixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = BlockedInstallation;
            fixture.DeviceBlocked = true;

            IActionResult result = initial
                ? await fixture.Controller.UploadPhoto(
                    [new FormFile(new MemoryStream([1]), 0, 1, "files", "photo.jpg")], cancellation.Token)
                : await fixture.Controller.FinalizeUpload(fixture.Prepare(4), cancellation.Token);

            var rejection = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, rejection.StatusCode);
            Assert.Equal("This device can't submit reports.", rejection.Value);
            fixture.BlockedDevices.Verify(d => d.IsBlockedAsync(BlockedInstallation, cancellation.Token), Times.Once);
            fixture.BlockedDevices.VerifyNoOtherCalls();
            fixture.Blobs.VerifyNoOtherCalls();
            fixture.Analysis.VerifyNoOtherCalls();
            fixture.Safety.VerifyNoOtherCalls();
            fixture.Authentication.VerifyNoOtherCalls();
            Assert.Empty(fixture.InitialMetadata);
            Assert.Null(fixture.Committed);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task WebsiteSubmissionsDoNotConsultTheBlocklist(bool initial)
        {
            using Fixture fixture = new Fixture();
            fixture.DeviceBlocked = true;

            var result = await Submit(fixture, initial);

            Assert.IsType<OkObjectResult>(result);
            fixture.BlockedDevices.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SubmissionFailsOpenIfCosmosDeniesBlocklistAccess(bool initial)
        {
            using Fixture fixture = new Fixture();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = BlockedInstallation;
            fixture.BlockedDevices.Setup(d => d.IsBlockedAsync(BlockedInstallation, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Cosmos.CosmosException("Private infrastructure detail",
                    HttpStatusCode.Forbidden, 0, "test", 0));

            Assert.IsType<OkObjectResult>(await Submit(fixture, initial));
            fixture.BlockedDevices.Verify(d => d.IsBlockedAsync(BlockedInstallation, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task FinalizeChecksOnceForTheWholeReportAndPreservesDeviceNormalization()
        {
            using Fixture fixture = new Fixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = "  Device-_1  ";
            var request = fixture.Prepare(4);
            foreach (var metadata in fixture.Metadata.Values)
            {
                metadata.DeviceId = "Device-_1";
            }

            Assert.IsType<OkObjectResult>(await fixture.Controller.FinalizeUpload(request, cancellation.Token));

            fixture.BlockedDevices.Verify(d => d.IsBlockedAsync("Device-_1", cancellation.Token), Times.Once);
            fixture.BlockedDevices.VerifyNoOtherCalls();
            Assert.Equal("Device-_1", fixture.LastDeviceId);
        }

        [Fact]
        public async Task InitialChecksOnceForTheWholeReport()
        {
            using Fixture fixture = new Fixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = BlockedInstallation;
            fixture.ConfigureInitial();
            byte[] jpeg = CreateJpeg();
            var photos = Enumerable.Range(0, 4).Select(i =>
                (IFormFile)new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", $"photo-{i}.jpg")).ToList();

            Assert.IsType<OkObjectResult>(await fixture.Controller.UploadPhoto(photos, cancellation.Token));

            fixture.BlockedDevices.Verify(d => d.IsBlockedAsync(BlockedInstallation, cancellation.Token), Times.Once);
            fixture.BlockedDevices.VerifyNoOtherCalls();
            Assert.Equal(4, fixture.InitialMetadata.Count);
        }

        [Fact]
        public async Task NewlyBlockedDeviceStillRecoversAcceptedReceipt()
        {
            using Fixture fixture = new Fixture();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = BlockedInstallation;
            fixture.Existing = new SubmissionReport(fixture.Receipt, BlockedInstallation, []);
            fixture.DeviceBlocked = true;

            var status = Assert.IsType<OkObjectResult>(await fixture.Controller.ReportStatus(Id));
            var retry = Assert.IsType<OkObjectResult>(await fixture.Controller.FinalizeUpload(
                new FinalizeReportRequest([], new ReportAttribution())));

            Assert.Equal(fixture.Receipt, status.Value);
            Assert.Equal(fixture.Receipt, retry.Value);
            fixture.BlockedDevices.VerifyNoOtherCalls();
            Assert.Null(fixture.Committed);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RequestCancellationDuringDeviceCheckPropagates(bool initial)
        {
            using Fixture fixture = new Fixture();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = BlockedInstallation;
            fixture.BlockedDevices.Setup(d => d.IsBlockedAsync(BlockedInstallation, cancellation.Token))
                .Callback(cancellation.Cancel)
                .ThrowsAsync(new OperationCanceledException(cancellation.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial
                ? fixture.Controller.UploadPhoto(
                    [new FormFile(new MemoryStream([1]), 0, 1, "files", "photo.jpg")], cancellation.Token)
                : fixture.Controller.FinalizeUpload(fixture.Prepare(), cancellation.Token));
            Assert.Null(fixture.Committed);
            fixture.Blobs.VerifyNoOtherCalls();
        }

        private static async Task<IActionResult> Submit(Fixture fixture, bool initial)
        {
            if (initial)
            {
                fixture.ConfigureInitial();
                byte[] jpeg = CreateJpeg();
                return await fixture.Controller.UploadPhoto(
                    [new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", "photo.jpg")]);
            }
            var request = fixture.Prepare(4);
            foreach (var metadata in fixture.Metadata.Values)
            {
                metadata.DeviceId = fixture.Controller.Request.Headers[UploadController.DeviceIdHeader].FirstOrDefault();
            }
            return await fixture.Controller.FinalizeUpload(request);
        }
    }
}
