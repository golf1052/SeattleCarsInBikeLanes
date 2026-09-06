namespace SeattleCarsInBikeLanes.Mobile.Services;

public interface IQueueRuntime
{
    DateTime UtcNow { get; }
    event EventHandler? ConnectivityChanged;
    void Dispatch(Action action);
    void Run(Func<Task> action);
}

public sealed class QueuedCredentialRejectedException : Exception;
