using Sentry;
using SeattleCarsInBikeLanes.Mobile.Core.Performance;

namespace SeattleCarsInBikeLanes.Mobile.Services;

public interface ICameraReadinessMetrics
{
    CameraReadinessTransition? ActiveTransition { get; }

    bool Begin(CameraReadinessTransition transition);

    Task<bool> EnsureCameraPermissionAsync();

    void Complete();

    void Finish(string result);
}

/// <summary>
/// Connects camera lifecycle measurements to Sentry application metrics.
/// </summary>
public sealed class CameraReadinessMetrics : ICameraReadinessMetrics
{
    private static readonly CameraReadinessCoordinator Coordinator =
        new CameraReadinessCoordinator(TimeProvider.System);

    private readonly IMobileMetricsEmitter emitter;
    private readonly string platform = DeviceInfo.Current.Platform.ToString().ToLowerInvariant();

    public CameraReadinessMetrics(IMobileMetricsEmitter emitter)
    {
        this.emitter = emitter;
    }

    public CameraReadinessTransition? ActiveTransition => Coordinator.ActiveTransition;

    /// <summary>
    /// Starts before MAUI dependency injection exists.
    /// </summary>
    public static void BeginColdStart()
    {
        Coordinator.Begin(CameraReadinessTransition.ColdStart);
    }

    public bool Begin(CameraReadinessTransition transition) => Coordinator.Begin(transition);

    public async Task<bool> EnsureCameraPermissionAsync()
    {
        PermissionStatus current = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (current == PermissionStatus.Granted)
        {
            Coordinator.GetActiveSession()?.MarkPermissionAlreadyGranted();
            return true;
        }

        if (current is PermissionStatus.Disabled or PermissionStatus.Restricted ||
            AppPreferences.CameraPermissionRequested ||
            (DeviceInfo.Current.Platform == DevicePlatform.iOS && current == PermissionStatus.Denied))
        {
            Finish("permission_denied");
            return false;
        }

        CameraReadinessSession? session = Coordinator.GetActiveSession();
        bool measuringPrompt = session?.TryBeginPermissionPrompt() == true;
        PermissionStatus requested = await Permissions.RequestAsync<Permissions.Camera>();
        AppPreferences.CameraPermissionRequested = true;
        bool granted = requested == PermissionStatus.Granted;

        if (measuringPrompt &&
            session!.TryEndPermissionPrompt(granted, out TimeSpan promptDuration))
        {
            emitter.Emit(CameraReadinessTelemetry.PermissionPrompt(
                session.Transition,
                granted,
                promptDuration,
                platform));
        }

        if (!granted)
        {
            Finish("permission_denied");
        }

        return granted;
    }

    public void Complete()
    {
        if (!Coordinator.TryComplete(out CameraReadinessMeasurement measurement))
        {
            return;
        }

        foreach (MobileMetricEvent metric in CameraReadinessTelemetry.Ready(measurement, platform))
        {
            emitter.Emit(metric);
        }
    }

    public void Finish(string result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);

        if (!Coordinator.TryFinish(
                out CameraReadinessTransition transition,
                out CameraPermissionState permissionState))
        {
            return;
        }

        emitter.Emit(CameraReadinessTelemetry.Outcome(
            transition,
            result,
            permissionState,
            platform));
    }
}
