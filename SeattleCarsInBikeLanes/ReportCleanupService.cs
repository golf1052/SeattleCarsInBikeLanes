using Azure;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes
{
    public sealed class ReportCleanupService : BackgroundService
    {
        private readonly ReportStore reports;
        private readonly ILogger<ReportCleanupService> logger;

        public ReportCleanupService(ReportStore reports, ILogger<ReportCleanupService> logger)
        {
            this.reports = reports;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            do
            {
                try
                {
                    await reports.CleanupAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is RequestFailedException or IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    logger.LogError(ex, "Report cleanup did not finish; durable cleanup state is retained for retry.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
