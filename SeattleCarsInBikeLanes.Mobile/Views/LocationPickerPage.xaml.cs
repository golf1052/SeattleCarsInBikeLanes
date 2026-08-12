using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using Map = Microsoft.Maui.Controls.Maps.Map;

namespace SeattleCarsInBikeLanes.Mobile.Views;

/// <summary>
/// Lets the user drop a pin when a photo has no usable location.
/// </summary>
/// <remarks>
/// Imported photos often have no GPS at all, and a photo the app took indoors or without a fix can
/// be missing one too. The site would otherwise reject the report with nothing the user can do
/// about it.
/// </remarks>
public partial class LocationPickerPage : ContentPage
{
    private readonly TaskCompletionSource<GeoPosition?> completion;
    private GeoPosition? selected;
    private bool closing;

    private LocationPickerPage(GeoPosition? initial, TaskCompletionSource<GeoPosition?> completion)
    {
        InitializeComponent();

        this.completion = completion;

        GeoPosition center = initial ?? BoundingBox.Seattle.Center;
        Map.MoveToRegion(MapSpan.FromCenterAndRadius(
            new Location(center.Latitude, center.Longitude),
            Distance.FromKilometers(initial.HasValue ? 0.5 : 8)));

        if (initial.HasValue)
        {
            SetSelection(initial.Value);
        }

        Map.MapClicked += MapClicked;
    }

    /// <summary>
    /// Shows the picker and waits for the user's answer.
    /// </summary>
    /// <returns>The chosen position, or null if the user backed out.</returns>
    public static async Task<GeoPosition?> PickAsync(GeoPosition? initial)
    {
        TaskCompletionSource<GeoPosition?> completion = new TaskCompletionSource<GeoPosition?>();
        LocationPickerPage page = new LocationPickerPage(initial, completion);

        await Shell.Current.Navigation.PushModalAsync(new NavigationPage(page));
        return await completion.Task;
    }

    private void MapClicked(object? sender, MapClickedEventArgs e) =>
        SetSelection(new GeoPosition(e.Location.Latitude, e.Location.Longitude));

    private void SetSelection(GeoPosition position)
    {
        selected = position;
        UseButton.IsEnabled = true;

        Map.Pins.Clear();
        Map.Pins.Add(new Pin()
        {
            Label = "Report location",
            Location = new Location(position.Latitude, position.Longitude)
        });
    }

    private async void CancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async void UseClicked(object? sender, EventArgs e) => await CloseAsync(selected);

    protected override bool OnBackButtonPressed()
    {
        // Dismissing without answering has to complete the task, or the caller waits forever.
        closing = true;
        completion.TrySetResult(null);
        return base.OnBackButtonPressed();
    }

    private async Task CloseAsync(GeoPosition? result)
    {
        // Buttons are not debounced, so a second tap during the dismiss animation would pop a page
        // that is already gone. From an async void handler that exception would take the app down.
        if (closing)
        {
            return;
        }

        closing = true;
        completion.TrySetResult(result);

        try
        {
            await Shell.Current.Navigation.PopModalAsync();
        }
        catch (Exception)
        {
            // The page is already on its way out, and the caller has its answer either way.
        }
    }
}
