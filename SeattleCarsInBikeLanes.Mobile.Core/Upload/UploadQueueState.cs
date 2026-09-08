namespace SeattleCarsInBikeLanes.Mobile.Core.Upload;

/// <summary>
/// Where a queued report has got to.
/// </summary>
/// <remarks>
/// Persisted as an integer, so the values are fixed.
/// </remarks>
public enum UploadQueueState
{
    /// <summary>
    /// Waiting to be sent, either for the first time or after a transient failure.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Being sent right now.
    /// </summary>
    /// <remarks>
    /// A report found in this state at startup was interrupted, by a crash or by the system killing
    /// the app, and goes back to <see cref="Pending"/>.
    /// </remarks>
    Uploading = 1,

    /// <summary>
    /// Given up on. Only the user can move it out of this state.
    /// </summary>
    Failed = 2
}

/// <summary>
/// Whether a failure is worth trying again.
/// </summary>
public enum UploadFailureKind
{
    /// <summary>
    /// Something that may well work on the next try: no signal, a timeout, a server having a
    /// moment.
    /// </summary>
    Transient,

    /// <summary>
    /// Something retrying cannot fix, which the user has to see and decide about.
    /// </summary>
    Permanent
}
