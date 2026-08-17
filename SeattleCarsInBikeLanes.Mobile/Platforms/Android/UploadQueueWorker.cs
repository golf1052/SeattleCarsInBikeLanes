using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

/// <summary>
/// Loads and drains the persisted upload queue while WorkManager holds the process awake.
/// </summary>
[Register("golf1052/seattlecarsinbikelanes/mobile/UploadQueueWorker")]
public sealed class UploadQueueWorker : Worker
{
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly CancellationTokenSource stopped = new CancellationTokenSource();

    public UploadQueueWorker(Context context, WorkerParameters parameters)
        : base(context, parameters)
    {
    }

    public override Result DoWork()
    {
        AndroidUploadQueueRuntime.WorkerStarted();

        try
        {
            IUploadQueue? queue = AndroidUploadQueueRuntime.GetUploadQueue();
            if (queue is null)
            {
                AndroidUploadQueueRuntime.GetWorkerLogger()?
                    .LogWarning("Upload worker could not resolve IUploadQueue from MAUI services.");
                return Result.InvokeRetry()!;
            }

            IAuthService? auth = AndroidUploadQueueRuntime.GetAuthService();
            if (auth is null)
            {
                AndroidUploadQueueRuntime.GetWorkerLogger()?
                    .LogWarning("Upload worker could not resolve IAuthService from MAUI services.");
                return Result.InvokeRetry()!;
            }

            RunAsync(queue, auth, stopped.Token).GetAwaiter().GetResult();

            // DrainAsync returns normally when every currently due report has been attempted.
            // Pending reports have their own persisted backoff; WorkManager's retry keeps the unique
            // wake-up alive until one becomes due, even if Android kills this process meanwhile.
            return queue.Reports.Any(report =>
                report.State is UploadQueueState.Pending or UploadQueueState.Uploading)
                ? Result.InvokeRetry()!
                : Result.InvokeSuccess()!;
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested)
        {
            return Result.InvokeRetry()!;
        }
        catch (Exception ex)
        {
            AndroidUploadQueueRuntime.GetWorkerLogger()?
                .LogError(ex, "Android background upload worker stopped unexpectedly.");
            return Result.InvokeRetry()!;
        }
        finally
        {
            AndroidUploadQueueRuntime.WorkerStopped();
        }
    }

    public override void OnStopped()
    {
        stopped.Cancel();
        base.OnStopped();
    }

    private static async Task RunAsync(
        IUploadQueue queue,
        IAuthService auth,
        CancellationToken cancellationToken)
    {
        // A WorkManager wake-up can be the first code to touch the queue in this process.
        await auth.InitializeAsync();
        await queue.StartAsync();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await queue.DrainAsync(cancellationToken);

            IReadOnlyList<QueuedReport> reports = queue.Reports;
            DateTime now = DateTime.UtcNow;
            bool anotherDrainIsActive = reports.Any(report =>
                report.State == UploadQueueState.Uploading);
            bool readyReportRemains = reports.Any(report =>
                report.State == UploadQueueState.Pending &&
                (report.NextAttemptAt is null || report.NextAttemptAt <= now));

            if (!anotherDrainIsActive && !readyReportRemains)
            {
                return;
            }

            // StartAsync also nudges the queue. If its fire-and-forget drain won the mutex before
            // this worker did, remain alive until that drain finishes rather than telling
            // WorkManager the job ended while an untracked upload is still using the process.
            await Task.Delay(DrainPollInterval, cancellationToken);
        }
    }
}
