using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile;

public partial class App : Application
{
	private readonly IUploadQueue uploadQueue;
	private readonly IAuthService authService;
	private readonly CameraAppLifecycle cameraLifecycle;
	private readonly ICameraReadinessMetrics cameraReadiness;
	private readonly ILogger<App> logger;

	public App(IUploadQueue uploadQueue,
		IAuthService authService,
		CameraAppLifecycle cameraLifecycle,
		ICameraReadinessMetrics cameraReadiness,
		ILogger<App> logger)
	{
		InitializeComponent();

		this.uploadQueue = uploadQueue;
		this.authService = authService;
		this.cameraLifecycle = cameraLifecycle;
		this.cameraReadiness = cameraReadiness;
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
		try
		{
			// The queue sends reports with nobody looking, and one the user asked to be credited
			// for has to know who they are without waiting for them to visit a page that happens
			// to refresh it.
			await authService.InitializeAsync();

			await uploadQueue.StartAsync();
		}
		catch (Exception ex)
		{
			// async void, so anything escaping here takes the app down on launch.
			logger.LogError(ex, "Failed to start the upload queue.");
		}
	}
}
