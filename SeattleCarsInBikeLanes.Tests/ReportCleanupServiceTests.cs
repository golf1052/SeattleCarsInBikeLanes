using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Tests
{
    public class ReportCleanupServiceTests
    {
        [Fact]
        public async Task InvalidRecordFailureIsLoggedWithoutStoppingTheService()
        {
            InvalidDataException failure = new InvalidDataException("Invalid report record.");
            Mock<ReportStore> reports = CreateReports(failure);
            TaskCompletionSource logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Mock<ILogger<ReportCleanupService>> logger = new Mock<ILogger<ReportCleanupService>>();
            logger.Setup(log => log.Log(LogLevel.Error, It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(), failure, It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(() =>
                {
                    logged.TrySetResult();
                });
            using ReportCleanupService service = new ReportCleanupService(reports.Object, logger.Object);

            try
            {
                await service.StartAsync(CancellationToken.None);
                await logged.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.NotNull(service.ExecuteTask);
                Assert.False(service.ExecuteTask.IsCompleted);
                reports.Verify(store => store.CleanupAsync(It.IsAny<CancellationToken>()), Times.Once);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task UnexpectedFailuresAreNotSwallowed()
        {
            InvalidOperationException failure = new InvalidOperationException("Unexpected cleanup bug.");
            Mock<ReportStore> reports = CreateReports(failure);
            using ReportCleanupService service = new ReportCleanupService(reports.Object, NullLogger<ReportCleanupService>.Instance);

            await service.StartAsync(CancellationToken.None);

            InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(failure, actual);
        }

        private static Mock<ReportStore> CreateReports(Exception failure)
        {
            Mock<ReportStore> reports = new Mock<ReportStore>(Mock.Of<BlobContainerClient>(), NullLogger<ReportStore>.Instance, TimeProvider.System);
            reports.Setup(store => store.CleanupAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            return reports;
        }
    }
}
