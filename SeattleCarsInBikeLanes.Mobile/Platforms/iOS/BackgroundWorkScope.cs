using Foundation;
using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Mobile.Services;
using UIKit;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

/// <summary>
/// Holds iOS off for the few seconds it takes to finish sending a report.
/// </summary>
/// <remarks>
/// A user reports a car and puts their phone straight back in their pocket, which is exactly when
/// iOS suspends the app. Without this the upload stops mid flight, and because the server discards
/// an unfinished upload after ten minutes the whole thing has to start again later. Asking for a
/// background task buys the roughly thirty seconds a report over a cellular connection needs.
/// </remarks>
public sealed class BackgroundWorkScope : IBackgroundWorkScope
{
    private readonly ILogger<BackgroundWorkScope> logger;

    public BackgroundWorkScope(ILogger<BackgroundWorkScope> logger)
    {
        this.logger = logger;
    }

    public async Task<IAsyncDisposable> BeginAsync(string name)
    {
        // UIApplication is main thread only, and the queue is drained from a background task.
        nint identifier = await MainThread.InvokeOnMainThreadAsync(() =>
            UIApplication.SharedApplication.BeginBackgroundTask(name, () =>
            {
                // Reaching here means the grace period ran out. The task is ended by Dispose in the
                // ordinary case, and there is nothing useful to do here beyond noting it: iOS kills
                // an app that is still holding the task at this point, so the report will be found
                // in the queue and started again next launch.
                logger.LogWarning("The background window for {Name} expired before the work finished.", name);
            }));

        return identifier == UIApplication.BackgroundTaskInvalid
            ? new NoScope()
            : new Scope(identifier);
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly nint identifier;

        public Scope(nint identifier)
        {
            this.identifier = identifier;
        }

        public async ValueTask DisposeAsync() =>
            await MainThread.InvokeOnMainThreadAsync(() =>
                UIApplication.SharedApplication.EndBackgroundTask(identifier));
    }

    private sealed class NoScope : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
