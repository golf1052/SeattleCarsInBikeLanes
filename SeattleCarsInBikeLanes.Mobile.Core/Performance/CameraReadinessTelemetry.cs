namespace SeattleCarsInBikeLanes.Mobile.Core.Performance;

public enum MobileMetricKind
{
    Counter,
    Distribution
}

public enum MobileMetricUnit
{
    None,
    Millisecond
}

public sealed record MobileMetricEvent(
    MobileMetricKind Kind,
    string Name,
    double Value,
    MobileMetricUnit Unit,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// Defines the camera metrics contract independently of either mobile platform or telemetry SDK.
/// </summary>
public static class CameraReadinessTelemetry
{
    public const string ReadinessDurationMetric = "mobile.camera.ready.duration";
    public const string PermissionDurationMetric = "mobile.camera.permission_prompt.duration";
    public const string ReadinessOutcomeMetric = "mobile.camera.ready.outcome";

    public static MobileMetricEvent PermissionPrompt(
        CameraReadinessTransition transition,
        bool granted,
        TimeSpan duration,
        string platform) =>
        new MobileMetricEvent(
            MobileMetricKind.Distribution,
            PermissionDurationMetric,
            duration.TotalMilliseconds,
            MobileMetricUnit.Millisecond,
            Attributes(
                transition,
                granted ? "granted" : "denied",
                granted ? CameraPermissionState.PromptGranted : CameraPermissionState.PromptDenied,
                platform));

    public static IReadOnlyList<MobileMetricEvent> Ready(
        CameraReadinessMeasurement measurement,
        string platform)
    {
        IReadOnlyDictionary<string, string> attributes = Attributes(
            measurement.Transition,
            "success",
            measurement.PermissionState,
            platform);

        return
        [
            new MobileMetricEvent(
                MobileMetricKind.Distribution,
                ReadinessDurationMetric,
                measurement.Duration.TotalMilliseconds,
                MobileMetricUnit.Millisecond,
                attributes),
            new MobileMetricEvent(
                MobileMetricKind.Counter,
                ReadinessOutcomeMetric,
                1,
                MobileMetricUnit.None,
                attributes)
        ];
    }

    public static MobileMetricEvent Outcome(
        CameraReadinessTransition transition,
        string result,
        CameraPermissionState permissionState,
        string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);

        return new MobileMetricEvent(
            MobileMetricKind.Counter,
            ReadinessOutcomeMetric,
            1,
            MobileMetricUnit.None,
            Attributes(transition, result, permissionState, platform));
    }

    private static IReadOnlyDictionary<string, string> Attributes(
        CameraReadinessTransition transition,
        string result,
        CameraPermissionState permissionState,
        string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["transition"] = TransitionName(transition),
            ["platform"] = platform.Trim().ToLowerInvariant(),
            ["permission_state"] = PermissionName(permissionState),
            ["result"] = result
        };
    }

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
