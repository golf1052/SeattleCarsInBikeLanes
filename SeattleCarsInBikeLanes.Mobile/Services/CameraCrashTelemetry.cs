using Sentry;

namespace SeattleCarsInBikeLanes.Mobile.Services;

internal enum CameraCrashPhase
{
    PageAppearing,
    PageDisappearing,
    OrientationSubscribing,
    OrientationSubscribed,
    OrientationUnsubscribing,
    OrientationUnsubscribed,
    OrientationChanged,
    ActivityOrientationChanged,
    PreviewStarting,
    FirstFrameWaiting,
    FirstFrameReady,
    FirstFrameCancelled,
    FirstFrameNotReady,
    PreviewReady,
    PreviewStopping,
    PreviewStopped,
    PreviewFailed,
    AppStopped,
    AppResumed
}

internal static class CameraCrashTelemetry
{
    public static void Record(CameraCrashPhase phase)
    {
        string state = phase.ToString();
        SentrySdk.AddBreadcrumb(state, category: "camera.lifecycle");
        SentrySdk.ConfigureScope(scope => scope.SetTag("camera.phase", state));
    }
}
