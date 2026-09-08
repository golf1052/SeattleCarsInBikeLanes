namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Keeps a system wake-up registered while the upload queue has work left to do.
/// </summary>
public interface IBackgroundUploadScheduler
{
    /// <summary>
    /// Ensures that one background upload wake-up is registered.
    /// </summary>
    void Schedule();

    /// <summary>
    /// Removes the registered wake-up when the queue no longer needs it.
    /// </summary>
    void Cancel();
}
