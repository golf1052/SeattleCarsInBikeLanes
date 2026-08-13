using System.Net;
using CommunityToolkit.Maui;
using Microsoft.Maui.Handlers;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Services;
using SeattleCarsInBikeLanes.Mobile.ViewModels;
using SeattleCarsInBikeLanes.Mobile.Views;

namespace SeattleCarsInBikeLanes.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Registering the schema once at startup, rather than when a page happens to be built,
		// means everything that reads or writes the flag agrees on the namespace.
		CarsInBikeLanesXmp.Register();

		UseMainFrameNavigationOnly();

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiCommunityToolkitCamera()
			.UseMauiApp<App>()
			.UseMauiMaps()
			.UseSentry(options =>
			{
				options.Dsn = "https://bc6091523e2aef695d08f3f657a3ca50@o4508715009572864.ingest.us.sentry.io/4508715013177344";
#if DEBUG
				options.Debug = true;
#endif
				options.TracesSampleRate = 1.0;
			})
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("FluentSystemIcons-Regular.ttf", "FluentIcons");
			});

		RegisterServices(builder.Services);
		RegisterViewModels(builder.Services);
		RegisterPages(builder.Services);

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	/// <summary>
	/// Reports only the visible page's navigations, not those of the frames inside it.
	/// </summary>
	/// <remarks>
	/// Both platforms ask whether to allow a navigation for every frame, so an embedded post counts
	/// as a navigation to another host. Without this the map's rule about opening off-site links in
	/// the browser fires on its own Twitter and Bluesky embeds and throws the user out of the app.
	/// </remarks>
	private static void UseMainFrameNavigationOnly()
	{
		WebViewHandler.Mapper.AppendToMapping("MainFrameNavigationOnly", (handler, _) =>
		{
#if IOS
			handler.PlatformView.NavigationDelegate = new Platforms.iOS.MainFrameNavigationDelegate(handler);
#elif ANDROID
			if (handler is WebViewHandler androidHandler)
			{
				handler.PlatformView.SetWebViewClient(new MainFrameWebViewClient(androidHandler));
			}
#endif
		});
	}

	private static void RegisterServices(IServiceCollection services)
	{
		services.AddSingleton(Geolocation.Default);

		// One cookie container, shared, so the session copied out of the web view is visible to
		// every request the app makes.
		services.AddSingleton<CookieContainer>();
		services.AddSingleton(serviceProvider => new HttpClient(new HttpClientHandler()
		{
			CookieContainer = serviceProvider.GetRequiredService<CookieContainer>(),
			UseCookies = true
		})
		{
			BaseAddress = SiteUrls.BaseAddress,

			// A report can be several megabytes over a phone connection, and the default timeout is
			// not generous enough for that on a weak signal.
			Timeout = TimeSpan.FromMinutes(3)
		});

		services.AddSingleton<IImportedPhotoStore>(_ => new ImportedPhotoStore(
			Path.Combine(FileSystem.AppDataDirectory, "importedphotos.db3")));

		services.AddSingleton<IUploadQueueStore>(_ => new UploadQueueStore(
			Path.Combine(FileSystem.AppDataDirectory, "uploadqueue.db3")));

		services.AddSingleton<IDeviceIdentityService, DeviceIdentityService>();
		services.AddSingleton<ICaptureService, CaptureService>();
		services.AddSingleton<IPhotoCatalog, PhotoCatalog>();
		services.AddSingleton<IAuthService, AuthService>();
		services.AddSingleton<IUploadService, UploadService>();

		// A singleton because a report outlives the page that created it. Everything about the
		// queue would be pointless if it died with the report sheet.
		services.AddSingleton<IUploadQueue, UploadQueue>();

#if IOS
		services.AddSingleton<IPhotoLibraryService, Platforms.iOS.PhotoLibraryService>();
		services.AddSingleton<IWebViewCookieBridge, Platforms.iOS.WebViewCookieBridge>();
		services.AddSingleton<IImageResizer, Platforms.iOS.ImageResizer>();
		services.AddSingleton<ICameraDeviceService, Platforms.iOS.CameraDeviceService>();
		services.AddSingleton<IBackgroundWorkScope, Platforms.iOS.BackgroundWorkScope>();
#else
		// The app is iOS first. These keep the other targets building, and make the gaps fail
		// visibly instead of looking like an empty photo library and a sign in that never works.
		services.AddSingleton<IPhotoLibraryService, UnsupportedPhotoLibraryService>();
		services.AddSingleton<IWebViewCookieBridge, NullWebViewCookieBridge>();
		services.AddSingleton<IImageResizer, PassthroughImageResizer>();
		services.AddSingleton<ICameraDeviceService, CameraDeviceService>();
		services.AddSingleton<IBackgroundWorkScope, NullBackgroundWorkScope>();
#endif
	}

	private static void RegisterViewModels(IServiceCollection services)
	{
		services.AddSingleton<CameraViewModel>();
		services.AddTransient<ReportViewModel>();
		services.AddSingleton<SettingsViewModel>();
	}

	private static void RegisterPages(IServiceCollection services)
	{
		services.AddSingleton<CameraPage>();
		services.AddSingleton<MapPage>();
		services.AddSingleton<SettingsPage>();
		services.AddTransient<ReportPage>();
		services.AddTransient<LoginPage>();
	}
}
