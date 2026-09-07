namespace SeattleCarsInBikeLanes.Platforms.iOS;

/// <summary>
/// Coordinates identifier initialization and release without depending on the platform API.
/// </summary>
/// <remarks>
/// The end callback runs on the calling thread. Callers must satisfy any platform thread affinity.
/// </remarks>
internal sealed class BackgroundTaskLifetime
{
    private readonly object gate = new object();
    private readonly nint invalidIdentifier;
    private readonly Action<nint> endTask;
    private nint identifier;
    private bool initialized;
    private bool endRequested;

    public BackgroundTaskLifetime(nint invalidIdentifier, Action<nint> endTask)
    {
        ArgumentNullException.ThrowIfNull(endTask);

        this.invalidIdentifier = invalidIdentifier;
        this.endTask = endTask;
        identifier = invalidIdentifier;
    }

    public void Initialize(nint identifier)
    {
        lock (gate)
        {
            if (initialized)
            {
                throw new InvalidOperationException("The background task identifier has already been initialized.");
            }

            initialized = true;
            if (!endRequested)
            {
                this.identifier = identifier;
                return;
            }
        }

        // Expiration may have requested release before the native begin call returned its identifier.
        if (identifier != invalidIdentifier)
        {
            endTask(identifier);
        }
    }

    public void End()
    {
        nint identifierToEnd;
        lock (gate)
        {
            endRequested = true;
            identifierToEnd = identifier;
            identifier = invalidIdentifier;
        }

        // Ownership is cleared before calling out, including when the callback reenters or throws.
        if (identifierToEnd != invalidIdentifier)
        {
            endTask(identifierToEnd);
        }
    }
}
