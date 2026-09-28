using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Google.Android.Material.Color;
using SeattleCarsInBikeLanes.Mobile.Platforms.Android;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile;

[Activity(Theme = "@style/App.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	private Orientation currentOrientation;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		// MAUI normally swaps the splash theme inside base.OnCreate, after Android's dynamic-color
		// callback has run. Set the final theme first so the wallpaper overlay is not discarded.
		SetTheme(Resource.Style.Maui_Material3_Theme_NoActionBar);
		DynamicColors.ApplyToActivityIfAvailable(this);
		AndroidMaterial3Theme.Apply(this);

		base.OnCreate(savedInstanceState);
		currentOrientation = Resources?.Configuration?.Orientation ?? Orientation.Undefined;
	}

	protected override void OnResume()
	{
		base.OnResume();
		AndroidMaterial3Theme.Apply(this);
	}

	public override void OnConfigurationChanged(Configuration newConfig)
	{
		if (newConfig.Orientation != currentOrientation)
		{
			currentOrientation = newConfig.Orientation;
			CameraCrashTelemetry.Record(CameraCrashPhase.ActivityOrientationChanged);
		}

		base.OnConfigurationChanged(newConfig);
		AndroidMaterial3Theme.Apply(this);
	}
}
