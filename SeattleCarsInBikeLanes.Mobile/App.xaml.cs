using SeattleCarsInBikeLanes.Mobile.Services;
using SeattleCarsInBikeLanes.Mobile.Core.Permissions;
#if ANDROID
using SeattleCarsInBikeLanes.Mobile.Resources.Styles;
#endif

namespace SeattleCarsInBikeLanes.Mobile;

public partial class App : Application
{
	private readonly IUploadQueue uploadQueue;
	private readonly IAuthService authService;
	private readonly CameraAppLifecycle cameraLifecycle;
	private readonly ICameraReadinessMetrics cameraReadiness;
	private readonly LaunchPermissionCoordinator launchPermissions;
	private readonly ILogger<App> logger;

	public App(IUploadQueue uploadQueue,
		IAuthService authService,
		CameraAppLifecycle cameraLifecycle,
		ICameraReadinessMetrics cameraReadiness,
		LaunchPermissionCoordinator launchPermissions,
		ILogger<App> logger)
	{
		InitializeComponent();

#if ANDROID
		Resources.MergedDictionaries.Add(new Material3Colors());
		Resources.MergedDictionaries.Add(new Material3Styles());
#endif

		this.uploadQueue = uploadQueue;
		this.authService = authService;
		this.cameraLifecycle = cameraLifecycle;
		this.cameraReadiness = cameraReadiness;
		this.launchPermissions = launchPermissions;
		this.logger = logger;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		AppShell shell = new AppShell();
		Window window = new Window(shell);
		bool cameraWasActiveWhenStopped = false;

		// Started from here rather than from a page, because the whole point of the queue is that a
		// report keeps going after the page that created it is gone.
		window.Created += (_, _) => Start();

		window.Stopped += (_, _) =>
		{
			cameraWasActiveWhenStopped = shell.CurrentPage is Views.CameraPage;
			if (cameraWasActiveWhenStopped)
			{
				cameraLifecycle.NotifyStopped();
			}
		};

		// Coming back to the app is a good moment to try the queue again too: it usually means the
		// phone is out of a pocket, awake, and back on a network.
		window.Resumed += (_, _) =>
		{
			if (cameraWasActiveWhenStopped)
			{
				cameraReadiness.Begin(Core.Performance.CameraReadinessTransition.AppResume);
				cameraLifecycle.NotifyResumed();
				cameraWasActiveWhenStopped = false;
			}

			uploadQueue.Kick();
		};

		return window;
	}

	private async void Start()
	{
		Task<LaunchPermissionSnapshot> permissionStartup = launchPermissions.InitializeAsync();

		try
		{
			// The queue sends reports with nobody looking, and one the user asked to be credited
			// for has to know who they are without waiting for them to visit a page that happens
			// to refresh it.
			await authService.InitializeAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to restore the active account.");
		}

		try
		{
			await uploadQueue.StartAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to start the upload queue.");
		}

		try
		{
			LaunchPermissionSnapshot permissions = await permissionStartup;
			LogPermissionError(permissions.Camera);
			LogPermissionError(permissions.PhotoLibrary);
			LogPermissionError(permissions.Location);
		}
		catch (Exception ex)
		{
			// Keep unexpected coordinator failures independent from the rest of app startup.
			logger.LogError(ex, "Failed to initialize launch permissions.");
		}
	}

	private void LogPermissionError(LaunchPermissionResult result)
	{
		if (result.Error is not null)
		{
			logger.LogError(result.Error,
				"Failed to initialize the {Permission} permission.",
				result.Permission);
		}
	}
}
