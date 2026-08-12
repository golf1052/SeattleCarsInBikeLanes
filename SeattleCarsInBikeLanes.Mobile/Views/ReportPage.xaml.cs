using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Services;
using SeattleCarsInBikeLanes.Mobile.ViewModels;

namespace SeattleCarsInBikeLanes.Mobile.Views;

/// <summary>
/// Where the user fills in and sends a report.
/// </summary>
/// <remarks>
/// The photos arrive as a list rather than a string, so this reads them with
/// <see cref="IQueryAttributable"/> instead of <c>QueryPropertyAttribute</c>. Shell runs every
/// non-string query property through <see cref="Convert.ChangeType(object, Type)"/>, which throws
/// for anything that isn't <see cref="IConvertible"/> and would take the whole navigation down.
/// </remarks>
public partial class ReportPage : ContentPage, IQueryAttributable
{
    public const string PhotosParameter = "photos";

    private readonly ReportViewModel viewModel;
    private readonly ILogger<ReportPage> logger;

    private bool loaded;

    public ReportPage(ReportViewModel viewModel, ILogger<ReportPage> logger)
    {
        InitializeComponent();

        this.viewModel = viewModel;
        this.logger = logger;

        BindingContext = viewModel;

        // A report is about something that already happened.
        DatePicker.MaximumDate = DateTime.Today;
    }

    public IReadOnlyList<ReportPhoto>? Photos { get; private set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue(PhotosParameter, out object? value) &&
            value is IReadOnlyList<ReportPhoto> photos)
        {
            Photos = photos;
            loaded = false;
        }
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);

        try
        {
            // Closing the location picker navigates back to this page, and reloading there would
            // throw away everything the user has typed, including the location they just picked.
            if (!loaded && Photos is { Count: > 0 })
            {
                loaded = true;
                await viewModel.LoadAsync(Photos);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to prepare the report page.");
        }
    }

    private async void PickLocationClicked(object? sender, EventArgs e)
    {
        try
        {
            GeoPosition? picked = await LocationPickerPage.PickAsync(viewModel.Location);
            if (picked.HasValue)
            {
                viewModel.SetLocation(picked, userSpecified: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to pick a location.");
        }
    }

    private async void SubmitClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await viewModel.SubmitAsync())
            {
                return;
            }

            await DisplayAlertAsync("Thanks", "Your report was submitted.", "OK");
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit a report.");
            await DisplayAlertAsync("Report", "Something went wrong submitting that report.", "OK");
        }
    }
}
