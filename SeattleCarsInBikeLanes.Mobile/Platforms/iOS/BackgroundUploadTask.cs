using BackgroundTasks;
using Foundation;
using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

/// <summary>
/// Sends queued reports long after the app stopped running.
/// </summary>
/// <remarks>
/// The background task taken while a report is in flight only covers the half minute after the user
/// leaves the app. A report that could not be sent then, because there was no signal in a tunnel or
/// a lift, would otherwise sit in the queue until the user happened to open the app again. A
/// processing task is how iOS lets an app finish that sort of work: the system wakes it when the
/// conditions the request asked for are met and gives it real time to run.
///
/// A background <see cref="NSUrlSession"/> would survive suspension outright, and was considered
/// first, but it does not fit this API. Sending a report is two dependent requests, and the server
/// deletes the blobs the first one produced after ten minutes, so a session resumed hours later
/// would finalize against photos that are no longer there.
/// </remarks>
public static class BackgroundUploadTask
{
    /// <summary>
    /// Also listed under BGTaskSchedulerPermittedIdentifiers in Info.plist. iOS refuses to register
    /// a task identifier that is not declared there.
    /// </summary>
    public const string Identifier = "golf1052.SeattleCarsInBikeLanes.Mobile.uploadqueue";

    /// <summary>
    /// How long the system is asked to wait before it considers running the task.
    /// </summary>
    /// <remarks>
    /// The in flight background task covers the moments right after the app is backgrounded, so
    /// there is no point asking to be woken inside that window.
    /// </remarks>
    private static readonly TimeSpan EarliestDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Tells iOS this app has a processing task, which has to happen before launching finishes.
    /// </summary>
    public static void Register(Func<IServiceProvider?> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        BGTaskScheduler.Shared.Register(Identifier, null, task => Run(task, services()));
    }

    /// <summary>
    /// Asks to be woken to finish the queue, if there is anything left in it.
    /// </summary>
    public static void Schedule(IServiceProvider? services)
    {
        IUploadQueue? queue = services?.GetService(typeof(IUploadQueue)) as IUploadQueue;
        ILogger? logger = services?.GetService(typeof(ILogger<BGTaskScheduler>)) as ILogger;

        // Nothing waiting means nothing to be woken for, and a request submitted anyway would only
        // spend the app's background budget on finding an empty queue.
        if (queue is null || !queue.Reports.Any(report => report.State != UploadQueueState.Failed))
        {
            return;
        }

        BGProcessingTaskRequest request = new BGProcessingTaskRequest(Identifier)
        {
            RequiresNetworkConnectivity = true,
            RequiresExternalPower = false,
            EarliestBeginDate = NSDate.FromTimeIntervalSinceNow(EarliestDelay.TotalSeconds)
        };

        // Replaces any request already pending, since they would do the same work.
        BGTaskScheduler.Shared.Cancel(Identifier);

        if (!BGTaskScheduler.Shared.Submit(request, out NSError? error) && error is not null)
        {
            logger?.LogWarning("Could not schedule the background upload task: {Error}", error.LocalizedDescription);
        }
    }

    private static void Run(BGTask task, IServiceProvider? services)
    {
        IUploadQueue? queue = services?.GetService(typeof(IUploadQueue)) as IUploadQueue;
        if (queue is null)
        {
            task.SetTaskCompleted(false);
            return;
        }

        CancellationTokenSource cancellation = new CancellationTokenSource();

        // iOS gives no warning beyond this before it kills the process, so the drain has to stop at
        // whatever it is doing. The report it was on is left in the queue and started again later.
        task.ExpirationHandler = () => cancellation.Cancel();

        _ = Task.Run(async () =>
        {
            bool completed = false;
            try
            {
                await queue.DrainAsync(cancellation.Token);
                completed = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // Swallowed deliberately: this runs with no user present, and an unhandled exception
                // here would be a crash on a system wake rather than a message anybody could act on.
            }
            finally
            {
                cancellation.Dispose();

                // Asking to be woken again, so a queue that could not be emptied is not stranded
                // until the user next opens the app.
                Schedule(services);

                MainThread.BeginInvokeOnMainThread(() => task.SetTaskCompleted(completed));
            }
        });
    }
}
