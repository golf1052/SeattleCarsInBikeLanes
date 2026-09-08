using SeattleCarsInBikeLanes.Mobile.Core.Navigation;
using SeattleCarsInBikeLanes.Mobile.ViewModels;

namespace SeattleCarsInBikeLanes.Mobile.Views;

/// <summary>
/// Account and app information.
/// </summary>
public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel viewModel;
    private readonly WebAuthActionCoordinator webAuthActions;
    private readonly ILogger<SettingsPage> logger;

    public SettingsPage(SettingsViewModel viewModel,
        WebAuthActionCoordinator webAuthActions,
        ILogger<SettingsPage> logger)
    {
        InitializeComponent();

        this.viewModel = viewModel;
        this.webAuthActions = webAuthActions;
        this.logger = logger;

        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            await viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load settings.");
        }
    }

    private async void BlueskySignInClicked(object? sender, EventArgs e) =>
        await OpenSignInAsync(WebAuthProvider.Bluesky);

    private async void MastodonSignInClicked(object? sender, EventArgs e) =>
        await OpenSignInAsync(WebAuthProvider.Mastodon);

    private async Task OpenSignInAsync(WebAuthProvider provider)
    {
        webAuthActions.QueueOpenSignIn(provider);

        try
        {
            await Shell.Current.GoToAsync(AppShell.MapRoute);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open the Map for {Provider} sign in.", provider);
        }
    }
}
