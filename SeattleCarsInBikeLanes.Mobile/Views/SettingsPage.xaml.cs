using SeattleCarsInBikeLanes.Mobile.ViewModels;

namespace SeattleCarsInBikeLanes.Mobile.Views;

/// <summary>
/// Account, device and app information.
/// </summary>
public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel viewModel;
    private readonly ILogger<SettingsPage> logger;

    public SettingsPage(SettingsViewModel viewModel, ILogger<SettingsPage> logger)
    {
        InitializeComponent();

        this.viewModel = viewModel;
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

    private async void SignInClicked(object? sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync(nameof(LoginPage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open the sign in page.");
        }
    }
}
