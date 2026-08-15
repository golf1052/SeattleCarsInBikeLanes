namespace SeattleCarsInBikeLanes.Mobile.Core.Performance;

public enum CameraReadinessTransition
{
    ColdStart,
    TabReturn,
    AppResume
}

public enum CameraPermissionState
{
    Unknown,
    AlreadyGranted,
    PromptGranted,
    PromptDenied
}

public readonly record struct CameraReadinessMeasurement(
    CameraReadinessTransition Transition,
    CameraPermissionState PermissionState,
    TimeSpan Duration);

/// <summary>
/// Measures one journey to a rendered, interactive camera preview.
/// </summary>
public sealed class CameraReadinessSession
{
    private readonly object gate = new object();
    private readonly TimeProvider timeProvider;
    private readonly long startedAt;

    private TimeSpan excludedDuration;
    private long? permissionPromptStartedAt;
    private bool finished;

    public CameraReadinessSession(CameraReadinessTransition transition, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        Transition = transition;
        this.timeProvider = timeProvider;
        startedAt = timeProvider.GetTimestamp();
    }

    public CameraReadinessTransition Transition { get; }

    public CameraPermissionState PermissionState { get; private set; }

    public bool IsFinished
    {
        get
        {
            lock (gate)
            {
                return finished;
            }
        }
    }

    public void MarkPermissionAlreadyGranted()
    {
        lock (gate)
        {
            if (!finished)
            {
                PermissionState = CameraPermissionState.AlreadyGranted;
            }
        }
    }

    public bool TryBeginPermissionPrompt()
    {
        lock (gate)
        {
            if (finished || permissionPromptStartedAt.HasValue)
            {
                return false;
            }

            permissionPromptStartedAt = timeProvider.GetTimestamp();
            return true;
        }
    }

    public bool TryEndPermissionPrompt(bool granted, out TimeSpan duration)
    {
        lock (gate)
        {
            if (finished || !permissionPromptStartedAt.HasValue)
            {
                duration = TimeSpan.Zero;
                return false;
            }

            long endedAt = timeProvider.GetTimestamp();
            duration = timeProvider.GetElapsedTime(permissionPromptStartedAt.Value, endedAt);
            excludedDuration += duration;
            permissionPromptStartedAt = null;
            PermissionState = granted
                ? CameraPermissionState.PromptGranted
                : CameraPermissionState.PromptDenied;

            return true;
        }
    }

    public bool TryComplete(out CameraReadinessMeasurement measurement)
    {
        lock (gate)
        {
            if (finished || permissionPromptStartedAt.HasValue)
            {
                measurement = default;
                return false;
            }

            finished = true;
            TimeSpan elapsed = timeProvider.GetElapsedTime(startedAt, timeProvider.GetTimestamp());
            TimeSpan adjusted = elapsed - excludedDuration;
            if (adjusted < TimeSpan.Zero)
            {
                adjusted = TimeSpan.Zero;
            }

            measurement = new CameraReadinessMeasurement(Transition, PermissionState, adjusted);
            return true;
        }
    }

    public bool TryFinishWithoutMeasurement()
    {
        lock (gate)
        {
            if (finished)
            {
                return false;
            }

            finished = true;
            permissionPromptStartedAt = null;
            return true;
        }
    }
}
