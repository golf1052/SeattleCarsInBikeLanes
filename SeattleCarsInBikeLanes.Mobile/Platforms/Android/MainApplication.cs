using Android.App;
using Android.Runtime;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override void OnCreate()
	{
		CameraReadinessMetrics.BeginColdStart();
		base.OnCreate();
		Platforms.Android.AndroidUploadQueueRuntime.Initialize(
			() => IPlatformApplication.Current?.Services);
	}
}
