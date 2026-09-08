namespace SeattleCarsInBikeLanes.Mobile.Core.Performance;

/// <summary>
/// Owns the single camera-readiness transition that can be active at a time.
/// </summary>
public sealed class CameraReadinessCoordinator
{
    private readonly object gate = new object();
    private readonly TimeProvider timeProvider;

    private CameraReadinessSession? activeSession;

    public CameraReadinessCoordinator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    public CameraReadinessTransition? ActiveTransition
    {
        get
        {
            lock (gate)
            {
                return activeSession?.Transition;
            }
        }
    }

    public bool Begin(CameraReadinessTransition transition)
    {
        lock (gate)
        {
            if (activeSession is null)
            {
                activeSession = new CameraReadinessSession(transition, timeProvider);
                return true;
            }

            // Window resume can race with Page.OnAppearing. The lifecycle transition is the more
            // specific description, so it replaces a tab-return timer that has not completed.
            if (transition == CameraReadinessTransition.AppResume &&
                activeSession.Transition == CameraReadinessTransition.TabReturn)
            {
                activeSession.TryFinishWithoutMeasurement();
                activeSession = new CameraReadinessSession(transition, timeProvider);
                return true;
            }

            return false;
        }
    }

    public CameraReadinessSession? GetActiveSession()
    {
        lock (gate)
        {
            return activeSession;
        }
    }

    public bool TryComplete(out CameraReadinessMeasurement measurement)
    {
        lock (gate)
        {
            if (activeSession is null)
            {
                measurement = default;
                return false;
            }

            if (!activeSession.TryComplete(out measurement))
            {
                return false;
            }

            activeSession = null;
            return true;
        }
    }

    public bool TryFinish(
        out CameraReadinessTransition transition,
        out CameraPermissionState permissionState)
    {
        lock (gate)
        {
            if (activeSession is null || !activeSession.TryFinishWithoutMeasurement())
            {
                transition = default;
                permissionState = default;
                return false;
            }

            transition = activeSession.Transition;
            permissionState = activeSession.PermissionState;
            activeSession = null;
            return true;
        }
    }
}
