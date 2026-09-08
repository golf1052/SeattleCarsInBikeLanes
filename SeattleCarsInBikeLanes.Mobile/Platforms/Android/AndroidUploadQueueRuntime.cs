using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

/// <summary>
/// Gives WorkManager's reflection-created worker access to the MAUI service provider.
/// </summary>
public static class AndroidUploadQueueRuntime
{
    private static readonly object gate = new object();

    private static Func<IServiceProvider?>? serviceProvider;
    private static IUploadQueue? observedQueue;
    private static IBackgroundUploadScheduler? scheduler;
    private static int runningWorkers;

    /// <summary>
    /// Connects the Android process lifetime to MAUI DI.
    /// </summary>
    /// <remarks>
    /// Call once from <c>MainApplication.OnCreate</c>, after <c>base.OnCreate()</c>. A factory is
    /// retained rather than a provider captured eagerly because WorkManager can create the process
    /// without creating an activity.
    /// </remarks>
    public static void Initialize(Func<IServiceProvider?> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        lock (gate)
        {
            serviceProvider = services;
        }

        EnsureQueueObserved();
    }

    internal static IUploadQueue? GetUploadQueue()
    {
        EnsureQueueObserved();

        lock (gate)
        {
            return observedQueue;
        }
    }

    internal static ILogger<UploadQueueWorker>? GetWorkerLogger() =>
        GetServices()?.GetService(typeof(ILogger<UploadQueueWorker>))
            as ILogger<UploadQueueWorker>;

    internal static IAuthService? GetAuthService() =>
        GetServices()?.GetService(typeof(IAuthService)) as IAuthService;

    internal static void WorkerStarted() => Interlocked.Increment(ref runningWorkers);

    internal static void WorkerStopped() => Interlocked.Decrement(ref runningWorkers);

    private static IServiceProvider? GetServices()
    {
        Func<IServiceProvider?>? factory;
        lock (gate)
        {
            factory = serviceProvider;
        }

        return factory?.Invoke();
    }

    private static void EnsureQueueObserved()
    {
        IServiceProvider? services = GetServices();
        IUploadQueue? queue = services?.GetService(typeof(IUploadQueue)) as IUploadQueue;
        IBackgroundUploadScheduler? resolvedScheduler =
            services?.GetService(typeof(IBackgroundUploadScheduler))
                as IBackgroundUploadScheduler;

        if (queue is null || resolvedScheduler is null)
        {
            return;
        }

        lock (gate)
        {
            if (ReferenceEquals(observedQueue, queue) &&
                ReferenceEquals(scheduler, resolvedScheduler))
            {
                return;
            }

            if (observedQueue is not null)
            {
                observedQueue.Changed -= QueueChanged;
            }

            observedQueue = queue;
            scheduler = resolvedScheduler;
            observedQueue.Changed += QueueChanged;
        }

        // StartAsync raises Changed after the persisted queue has been loaded. Reconciling before
        // that would see an empty in-memory queue and could cancel the WorkManager job that launched
        // this headless process.
    }

    private static void QueueChanged(object? sender, EventArgs e) => Reconcile();

    private static void Reconcile()
    {
        IUploadQueue? queue;
        IBackgroundUploadScheduler? backgroundScheduler;

        lock (gate)
        {
            queue = observedQueue;
            backgroundScheduler = scheduler;
        }

        if (queue is null || backgroundScheduler is null)
        {
            return;
        }

        try
        {
            if (queue.Reports.Any(report => report.State != UploadQueueState.Failed))
            {
                backgroundScheduler.Schedule();
            }
            else if (Volatile.Read(ref runningWorkers) == 0)
            {
                backgroundScheduler.Cancel();
            }
        }
        catch (Exception ex)
        {
            (GetServices()?.GetService(typeof(ILogger<WorkManagerUploadScheduler>))
                as ILogger<WorkManagerUploadScheduler>)?
                .LogWarning(ex, "Could not update the Android background upload request.");
        }
    }
}
