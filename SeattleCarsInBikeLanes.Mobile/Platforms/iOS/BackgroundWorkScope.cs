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
/// an unfinished upload after ten minutes the whole thing has to start again later. A background
/// task gives an upload begun while the app was active a grace period to finish.
///
/// Apple does not guarantee a fixed duration. Apple Developer Technical Support (DTS) engineers
/// describe the currently observed allowance as roughly thirty seconds. The actual duration is
/// system-determined and may be shorter. Once the app is in the background,
/// <see cref="UIApplication.BackgroundTimeRemaining"/> is the runtime source of the remaining
/// allowance, not a guarantee that execution will continue for that long. This is only a grace
/// period; the persistent upload queue must still recover from interruption.
///
/// See https://developer.apple.com/documentation/uikit/extending-your-app-s-background-execution-time
/// and https://developer.apple.com/forums/thread/85066.
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
        // Keep native begin/end calls on the main thread, where UIKit invokes expiration.
        return await MainThread.InvokeOnMainThreadAsync(() =>
        {
            UIApplication application = UIApplication.SharedApplication;
            BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(
                UIApplication.BackgroundTaskInvalid,
                application.EndBackgroundTask);
            Scope scope = new Scope(lifetime);

            nint identifier = application.BeginBackgroundTask(name, () =>
            {
                try
                {
                    logger.LogWarning("The background window for {Name} expired before the work finished.", name);
                }
                finally
                {
                    lifetime.End();
                }
            });

            lifetime.Initialize(identifier);
            return scope;
        });
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly BackgroundTaskLifetime lifetime;

        public Scope(BackgroundTaskLifetime lifetime)
        {
            this.lifetime = lifetime;
        }

        public async ValueTask DisposeAsync() =>
            await MainThread.InvokeOnMainThreadAsync(lifetime.End);
    }
}
