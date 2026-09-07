using SeattleCarsInBikeLanes.Mobile.Core.Performance;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class CameraReadinessSessionTests
{
    [Fact]
    public void CompletesOnlyOnce()
    {
        ManualTimeProvider time = new ManualTimeProvider();
        CameraReadinessSession session = new CameraReadinessSession(CameraReadinessTransition.ColdStart, time);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(session.TryComplete(out CameraReadinessMeasurement measurement));
        Assert.Equal(TimeSpan.FromSeconds(2), measurement.Duration);
        Assert.False(session.TryComplete(out _));
    }

    [Fact]
    public void ExcludesPermissionPromptFromReadinessDuration()
    {
        ManualTimeProvider time = new ManualTimeProvider();
        CameraReadinessSession session = new CameraReadinessSession(CameraReadinessTransition.ColdStart, time);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(session.TryBeginPermissionPrompt());
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(session.TryEndPermissionPrompt(granted: true, out TimeSpan promptDuration));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(session.TryComplete(out CameraReadinessMeasurement measurement));
        Assert.Equal(TimeSpan.FromSeconds(5), promptDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), measurement.Duration);
        Assert.Equal(CameraPermissionState.PromptGranted, measurement.PermissionState);
    }

    [Fact]
    public void CannotCompleteWhilePermissionPromptIsActive()
    {
        CameraReadinessSession session = new CameraReadinessSession(
            CameraReadinessTransition.ColdStart,
            new ManualTimeProvider());

        Assert.True(session.TryBeginPermissionPrompt());
        Assert.False(session.TryComplete(out _));
    }

    [Fact]
    public void TerminalOutcomeSuppressesLaterCompletion()
    {
        CameraReadinessSession session = new CameraReadinessSession(
            CameraReadinessTransition.AppResume,
            new ManualTimeProvider());

        Assert.True(session.TryFinishWithoutMeasurement());
        Assert.False(session.TryFinishWithoutMeasurement());
        Assert.False(session.TryComplete(out _));
    }

    [Fact]
    public void PreservesTransitionAndExistingPermission()
    {
        ManualTimeProvider time = new ManualTimeProvider();
        CameraReadinessSession session = new CameraReadinessSession(CameraReadinessTransition.TabReturn, time);
        session.MarkPermissionAlreadyGranted();
        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.True(session.TryComplete(out CameraReadinessMeasurement measurement));
        Assert.Equal(CameraReadinessTransition.TabReturn, measurement.Transition);
        Assert.Equal(CameraPermissionState.AlreadyGranted, measurement.PermissionState);
        Assert.Equal(TimeSpan.FromMilliseconds(250), measurement.Duration);
    }

    [Fact]
    public void GrantedStatusCheckDoesNotOverwritePromptResult()
    {
        CameraReadinessSession session = new CameraReadinessSession(
            CameraReadinessTransition.ColdStart,
            new ManualTimeProvider());

        Assert.True(session.TryBeginPermissionPrompt());
        Assert.True(session.TryEndPermissionPrompt(granted: true, out _));
        session.MarkPermissionAlreadyGranted();

        Assert.Equal(CameraPermissionState.PromptGranted, session.PermissionState);
    }

    [Fact]
    public void ExcludesOtherLaunchPermissionPromptsFromReadinessDuration()
    {
        ManualTimeProvider time = new ManualTimeProvider();
        CameraReadinessSession session = new CameraReadinessSession(
            CameraReadinessTransition.ColdStart,
            time);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(session.TryBeginExcludedDelay());
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.True(session.TryEndExcludedDelay());
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(session.TryComplete(out CameraReadinessMeasurement measurement));
        Assert.Equal(TimeSpan.FromSeconds(3), measurement.Duration);
    }

    [Fact]
    public void AppResumeReplacesOverlappingTabReturn()
    {
        ManualTimeProvider time = new ManualTimeProvider();
        CameraReadinessCoordinator coordinator = new CameraReadinessCoordinator(time);

        Assert.True(coordinator.Begin(CameraReadinessTransition.TabReturn));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(coordinator.Begin(CameraReadinessTransition.AppResume));
        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.True(coordinator.TryComplete(out CameraReadinessMeasurement measurement));
        Assert.Equal(CameraReadinessTransition.AppResume, measurement.Transition);
        Assert.Equal(TimeSpan.FromMilliseconds(250), measurement.Duration);
    }

    [Fact]
    public void TabReturnDoesNotReplaceColdStart()
    {
        CameraReadinessCoordinator coordinator = new CameraReadinessCoordinator(new ManualTimeProvider());

        Assert.True(coordinator.Begin(CameraReadinessTransition.ColdStart));
        Assert.False(coordinator.Begin(CameraReadinessTransition.TabReturn));
        Assert.Equal(CameraReadinessTransition.ColdStart, coordinator.ActiveTransition);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
