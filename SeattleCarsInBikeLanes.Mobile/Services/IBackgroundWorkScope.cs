namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Asks the platform to keep the app running for a moment after it stops being the thing on screen.
/// </summary>
/// <remarks>
/// iOS suspends an app very shortly after it goes to the background, which would leave a report
/// half sent: the photos uploaded, the finalize call never made, and the server's copy of the
/// upload thrown away ten minutes later. Asking for a background task buys the seconds needed to
/// finish the one that is already in flight. It is only ever a grace period, not a guarantee, so
/// the queue still has to cope with being cut off part way through.
/// </remarks>
public interface IBackgroundWorkScope
{
    /// <summary>
    /// Asks for time to finish a piece of work. Disposing the result gives it back.
    /// </summary>
    /// <remarks>
    /// The time has to be handed back promptly. iOS kills an app that holds a background task past
    /// its expiry rather than merely suspending it.
    /// </remarks>
    Task<IAsyncDisposable> BeginAsync(string name);
}

/// <summary>
/// The fallback for platforms with no such concept, or no need for one.
/// </summary>
public sealed class NullBackgroundWorkScope : IBackgroundWorkScope
{
    private static readonly IAsyncDisposable Scope = new NoScope();

    public Task<IAsyncDisposable> BeginAsync(string name) => Task.FromResult(Scope);

    private sealed class NoScope : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
