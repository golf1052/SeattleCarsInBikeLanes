using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile;

public partial class App : Application
{
	private readonly IUploadQueue uploadQueue;
	private readonly IAuthService authService;
	private readonly ILogger<App> logger;

	public App(IUploadQueue uploadQueue, IAuthService authService, ILogger<App> logger)
	{
		InitializeComponent();

		this.uploadQueue = uploadQueue;
		this.authService = authService;
		this.logger = logger;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		Window window = new Window(new AppShell());

		// Started from here rather than from a page, because the whole point of the queue is that a
		// report keeps going after the page that created it is gone.
		window.Created += (_, _) => Start();

		// Coming back to the app is a good moment to try again: it usually means the phone is out
		// of a pocket, awake, and back on a network.
		window.Resumed += (_, _) => uploadQueue.Kick();

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
