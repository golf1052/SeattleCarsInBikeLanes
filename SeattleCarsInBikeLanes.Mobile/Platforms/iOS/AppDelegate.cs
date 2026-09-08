using Foundation;
using SeattleCarsInBikeLanes.Mobile.Services;
using UIKit;

namespace SeattleCarsInBikeLanes.Mobile;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	/// <summary>
	/// Registers the background task that finishes sending queued reports.
	/// </summary>
	/// <remarks>
	/// iOS requires every task identifier to be registered before launching finishes, so this cannot
	/// wait until something needs it. The service provider is resolved lazily, when the task
	/// actually runs, because the handler outlives any particular launch.
	/// </remarks>
	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		CameraReadinessMetrics.BeginColdStart();
		bool result = base.FinishedLaunching(application, launchOptions);

		Platforms.iOS.BackgroundUploadTask.Register(() => IPlatformApplication.Current?.Services);

		return result;
	}

	/// <summary>
	/// Asks to be woken later if the queue still has reports in it.
	/// </summary>
	/// <remarks>
	/// The scope taken around an in flight report covers the seconds right after this, which is
	/// enough for a report being sent as the user pockets their phone. This is for the rest: a
	/// report with no signal to send over yet, which would otherwise wait for the user to open the
	/// app again.
	/// </remarks>
	public override void DidEnterBackground(UIApplication application)
	{
		base.DidEnterBackground(application);

		Platforms.iOS.BackgroundUploadTask.Schedule(IPlatformApplication.Current?.Services);
	}
}
