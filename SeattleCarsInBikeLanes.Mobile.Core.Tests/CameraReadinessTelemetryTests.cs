using SeattleCarsInBikeLanes.Mobile.Core.Performance;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class CameraReadinessTelemetryTests
{
    [Theory]
    [InlineData("ios")]
    [InlineData("android")]
    public void ReadyUsesSharedMetricContract(string platform)
    {
        CameraReadinessMeasurement measurement = new CameraReadinessMeasurement(
            CameraReadinessTransition.AppResume,
            CameraPermissionState.AlreadyGranted,
            TimeSpan.FromMilliseconds(425));

        IReadOnlyList<MobileMetricEvent> metrics = CameraReadinessTelemetry.Ready(measurement, platform);

        Assert.Collection(metrics,
            duration =>
            {
                Assert.Equal(MobileMetricKind.Distribution, duration.Kind);
                Assert.Equal(CameraReadinessTelemetry.ReadinessDurationMetric, duration.Name);
                Assert.Equal(425, duration.Value);
                Assert.Equal(MobileMetricUnit.Millisecond, duration.Unit);
                AssertAttributes(duration, platform, "app_resume", "already_granted", "success");
            },
            outcome =>
            {
                Assert.Equal(MobileMetricKind.Counter, outcome.Kind);
                Assert.Equal(CameraReadinessTelemetry.ReadinessOutcomeMetric, outcome.Name);
                Assert.Equal(1, outcome.Value);
                Assert.Equal(MobileMetricUnit.None, outcome.Unit);
                AssertAttributes(outcome, platform, "app_resume", "already_granted", "success");
            });
    }

    [Theory]
    [InlineData("ios")]
    [InlineData("android")]
    public void PermissionPromptUsesSharedMetricContract(string platform)
    {
        MobileMetricEvent metric = CameraReadinessTelemetry.PermissionPrompt(
            CameraReadinessTransition.ColdStart,
            granted: false,
            TimeSpan.FromSeconds(2),
            platform);

        Assert.Equal(MobileMetricKind.Distribution, metric.Kind);
        Assert.Equal(CameraReadinessTelemetry.PermissionDurationMetric, metric.Name);
        Assert.Equal(2000, metric.Value);
        Assert.Equal(MobileMetricUnit.Millisecond, metric.Unit);
        AssertAttributes(metric, platform, "cold_start", "prompt_denied", "denied");
    }

    [Theory]
    [InlineData(CameraReadinessTransition.ColdStart, "cold_start")]
    [InlineData(CameraReadinessTransition.TabReturn, "tab_return")]
    [InlineData(CameraReadinessTransition.AppResume, "app_resume")]
    public void TerminalOutcomesCoverEveryTransition(
        CameraReadinessTransition transition,
        string expectedTransition)
    {
        foreach (string result in new[] { "permission_denied", "no_camera", "error", "cancelled" })
        {
            MobileMetricEvent metric = CameraReadinessTelemetry.Outcome(
                transition,
                result,
                CameraPermissionState.Unknown,
                "android");

            AssertAttributes(metric, "android", expectedTransition, "unknown", result);
        }
    }

    [Fact]
    public void PlatformsDifferOnlyByPlatformAttribute()
    {
        CameraReadinessMeasurement measurement = new CameraReadinessMeasurement(
            CameraReadinessTransition.TabReturn,
            CameraPermissionState.PromptGranted,
            TimeSpan.FromMilliseconds(100));

        MobileMetricEvent ios = CameraReadinessTelemetry.Ready(measurement, "ios")[0];
        MobileMetricEvent android = CameraReadinessTelemetry.Ready(measurement, "android")[0];

        Assert.Equal(ios with { Attributes = android.Attributes }, android);
        Assert.Equal("ios", ios.Attributes["platform"]);
        Assert.Equal("android", android.Attributes["platform"]);
    }

    private static void AssertAttributes(
        MobileMetricEvent metric,
        string platform,
        string transition,
        string permission,
        string result)
    {
        Assert.Equal(4, metric.Attributes.Count);
        Assert.Equal(platform, metric.Attributes["platform"]);
        Assert.Equal(transition, metric.Attributes["transition"]);
        Assert.Equal(permission, metric.Attributes["permission_state"]);
        Assert.Equal(result, metric.Attributes["result"]);
    }
}
