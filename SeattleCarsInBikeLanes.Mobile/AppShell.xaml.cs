using SeattleCarsInBikeLanes.Mobile.Views;

namespace SeattleCarsInBikeLanes.Mobile;

public partial class AppShell : Shell
{
	public const string MapRoute = "//MainTabs/MapTab/MapPage";

	public AppShell()
	{
		InitializeComponent();

		// Pages pushed on top of a tab rather than being tabs themselves.
		Routing.RegisterRoute(nameof(ReportPage), typeof(ReportPage));
	}
}
