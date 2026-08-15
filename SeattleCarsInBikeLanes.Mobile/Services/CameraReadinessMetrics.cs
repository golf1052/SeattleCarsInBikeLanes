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
    private const string ReadinessDurationMetric = "mobile.camera.ready.duration";
    private const string PermissionDurationMetric = "mobile.camera.permission_prompt.duration";
    private const string ReadinessOutcomeMetric = "mobile.camera.ready.outcome";

    private static readonly CameraReadinessCoordinator Coordinator =
        new CameraReadinessCoordinator(TimeProvider.System);

    private readonly string platform = DeviceInfo.Current.Platform.ToString().ToLowerInvariant();

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
            SentrySdk.Metrics.EmitDistribution(PermissionDurationMetric,
                promptDuration.TotalMilliseconds,
                MeasurementUnit.Duration.Millisecond,
                Attributes(
                    session.Transition,
                    granted ? "granted" : "denied",
                    granted ? CameraPermissionState.PromptGranted : CameraPermissionState.PromptDenied));
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

        KeyValuePair<string, object>[] attributes =
            Attributes(measurement.Transition, "success", measurement.PermissionState);

        SentrySdk.Metrics.EmitDistribution(ReadinessDurationMetric,
            measurement.Duration.TotalMilliseconds,
            MeasurementUnit.Duration.Millisecond,
            attributes);
        SentrySdk.Metrics.EmitCounter(ReadinessOutcomeMetric, 1, attributes);
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

        SentrySdk.Metrics.EmitCounter(ReadinessOutcomeMetric,
            1,
            Attributes(transition, result, permissionState));
    }

    private KeyValuePair<string, object>[] Attributes(
        CameraReadinessTransition transition,
        string result,
        CameraPermissionState permissionState) =>
    [
        new KeyValuePair<string, object>("transition", TransitionName(transition)),
        new KeyValuePair<string, object>("platform", platform),
        new KeyValuePair<string, object>("permission_state", PermissionName(permissionState)),
        new KeyValuePair<string, object>("result", result)
    ];

    private static string TransitionName(CameraReadinessTransition transition) => transition switch
    {
        CameraReadinessTransition.ColdStart => "cold_start",
        CameraReadinessTransition.TabReturn => "tab_return",
        CameraReadinessTransition.AppResume => "app_resume",
        _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null)
    };

    private static string PermissionName(CameraPermissionState permissionState) => permissionState switch
    {
        CameraPermissionState.Unknown => "unknown",
        CameraPermissionState.AlreadyGranted => "already_granted",
        CameraPermissionState.PromptGranted => "prompt_granted",
        CameraPermissionState.PromptDenied => "prompt_denied",
        _ => throw new ArgumentOutOfRangeException(nameof(permissionState), permissionState, null)
    };
}
