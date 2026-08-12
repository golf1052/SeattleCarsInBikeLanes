using System.ComponentModel;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using SeattleCarsInBikeLanes.Mobile.Services;
using SeattleCarsInBikeLanes.Mobile.ViewModels;

namespace SeattleCarsInBikeLanes.Mobile.Views;

/// <summary>
/// The camera, which is the app's start page.
/// </summary>
public partial class CameraPage : ContentPage
{
    /// <summary>
    /// How far a pinch is allowed to close before it stops counting.
    /// </summary>
    /// <remarks>
    /// Only guards against the running total reaching zero, which would strand the gesture with
    /// nothing left to multiply back up.
    /// </remarks>
    private const double MinimumPinchScale = 0.01;

    private readonly CameraViewModel viewModel;
    private readonly ICameraProvider cameraProvider;
    private readonly ICameraDeviceService cameraDevices;
    private readonly ILogger<CameraPage> logger;

    private CameraView? camera;

    /// <summary>
    /// The cameras the switch button moves between, rear first.
    /// </summary>
    private IReadOnlyList<CameraInfo> selectableCameras = [];

    private int currentCameraIndex;

    /// <summary>
    /// Whether the user has pointed the current camera at something themselves.
    /// </summary>
    /// <remarks>
    /// Their choice outranks the app's, so once they have tapped to focus nothing here puts the
    /// camera back to metering on the middle of the frame until they switch cameras.
    /// </remarks>
    private bool hasManualFocusPoint;

    /// <summary>
    /// Which tap the reticle currently belongs to.
    /// </summary>
    /// <remarks>
    /// Its fade runs on a delay, so without this a tap during the wait would have the previous
    /// tap's fade hide the reticle out from under it.
    /// </remarks>
    private int focusReticleGeneration;

    /// <summary>
    /// The zoom the current pinch started from, and how far the fingers have moved since.
    /// </summary>
    private float pinchStartZoom = 1f;

    private double pinchScale = 1;

    public CameraPage(CameraViewModel viewModel,
        ICameraProvider cameraProvider,
        ICameraDeviceService cameraDevices,
        ILogger<CameraPage> logger)
    {
        InitializeComponent();

        this.viewModel = viewModel;
        this.cameraProvider = cameraProvider;
        this.cameraDevices = cameraDevices;
        this.logger = logger;

        BindingContext = viewModel;
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);

        try
        {
            await viewModel.LoadAsync();
            await SetUpCamerasAsync();

            // Every other camera app opens at 1x, and a zoom left over from a shot taken minutes
            // ago is a crop the user did not ask for and might not notice until afterwards.
            viewModel.ResetZoom();
        }
        catch (Exception ex)
        {
            // OnNavigatedTo is async void, so anything escaping here would take the app down.
            logger.LogError(ex, "Failed to prepare the camera page.");
        }
    }

    /// <summary>
    /// Attaches the camera preview, if this device actually has a camera.
    /// </summary>
    /// <remarks>
    /// The preview is created here rather than in the XAML because the toolkit's handler throws
    /// from an async void the moment it connects to a device with no camera, which no caller can
    /// catch and which crashes the app on launch. A simulator is the obvious case, but so is any
    /// device whose camera is unavailable.
    /// </remarks>
    private async Task SetUpCamerasAsync()
    {
        await cameraProvider.RefreshAvailableCameras(CancellationToken.None);

        IReadOnlyList<CameraInfo>? cameras = cameraProvider.AvailableCameras;
        if (cameras is not { Count: > 0 })
        {
            CaptureButton.IsEnabled = false;
            SwitchCameraButton.IsEnabled = false;
            viewModel.HasCamera = false;
            return;
        }

        selectableCameras = BuildSelectableCameras(cameras);

        viewModel.HasCamera = true;
        CaptureButton.IsEnabled = true;
        SwitchCameraButton.IsEnabled = selectableCameras.Count > 1;

        if (camera is null)
        {
            camera = new CameraView();
            camera.MediaCaptured += CameraMediaCaptured;

            // Two way so the toolkit's own reset to 1x, which it does every time a camera finishes
            // loading, comes back to the view model instead of leaving the label wrong.
            camera.SetBinding(CameraView.ZoomFactorProperty,
                new Binding(nameof(CameraViewModel.ZoomFactor), BindingMode.TwoWay));

            camera.PropertyChanged += CameraPropertyChanged;

            // Chosen before the view is attached, because the toolkit falls back to whatever camera
            // happens to be first in the platform's list if nothing has been picked by the time the
            // preview connects, and on an iPhone that list starts with a lens nobody asked for.
            camera.SelectedCamera = selectableCameras.FirstOrDefault();

            CameraHost.Add(camera);
        }

        if (camera.SelectedCamera is null)
        {
            return;
        }

        UpdateSelectedCamera(camera.SelectedCamera);
    }

    /// <summary>
    /// Works out the cameras the switch button should move between.
    /// </summary>
    /// <remarks>
    /// The platform's list is not a list of cameras in the sense a user means. An iPhone reports
    /// the wide, ultra wide and telephoto lenses separately and then reports the virtual devices
    /// that combine them, so cycling through all of it takes six or more taps to get back to where
    /// you started, most of them landing somewhere unrecognisable. One camera each way is what the
    /// button is for.
    /// </remarks>
    private IReadOnlyList<CameraInfo> BuildSelectableCameras(IReadOnlyList<CameraInfo> cameras)
    {
        List<CameraInfo> selectable = new List<CameraInfo>(2);

        // Rear first: it is what the app opens on, and it is the one used to report anything.
        foreach (CameraPosition position in new[] { CameraPosition.Rear, CameraPosition.Front })
        {
            CameraInfo? main = cameraDevices.GetMainCamera(cameras, position);
            if (main is not null && !selectable.Contains(main))
            {
                selectable.Add(main);
            }
        }

        // A device that says nothing useful about which way its cameras face would otherwise be
        // left with no camera at all, which is worse than an unknown one.
        if (selectable.Count == 0)
        {
            selectable.Add(cameras[0]);
        }

        return selectable;
    }

    private void CameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CameraView.SelectedCamera))
        {
            return;
        }

        // The toolkit picks the default camera from inside its own connect sequence, which on
        // Android runs on a CameraX executor rather than the UI thread. Taking the range from there
        // moves bound properties and writes back to the preview, so it has to be marshalled.
        if (MainThread.IsMainThread)
        {
            UpdateSelectedCamera(camera?.SelectedCamera);
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => UpdateSelectedCamera(camera?.SelectedCamera));
        }
    }

    /// <summary>
    /// Catches everything up with the camera now being previewed.
    /// </summary>
    private void UpdateSelectedCamera(CameraInfo? selected)
    {
        HideFocusReticle();
        hasManualFocusPoint = false;

        if (selected is null)
        {
            viewModel.SetZoomRange(1f, 1f);
            return;
        }

        // Keeps the switch button in step, including with the camera the toolkit picks for itself
        // when the preview first connects.
        int index = IndexOfSelectable(selected);
        if (index >= 0)
        {
            currentCameraIndex = index;
        }

        viewModel.SetZoomRange(selected.MinimumZoomFactor, selected.MaximumZoomFactor);
        cameraDevices.ResumeContinuousFocus(selected.DeviceId);
    }

    private int IndexOfSelectable(CameraInfo selected)
    {
        for (int i = 0; i < selectableCameras.Count; i++)
        {
            if (string.Equals(selectableCameras[i].DeviceId, selected.DeviceId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Focuses and meters where the user tapped the preview.
    /// </summary>
    private async void PreviewTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (camera?.SelectedCamera is null)
            {
                return;
            }

            // A tap on the zoom pill reaches this handler as well, because iOS offers the touch to
            // the recognisers on every view above the one that was hit. Without this, changing the
            // zoom would also focus the camera on the bottom of the frame.
            Point? overlayPoint = e.GetPosition(PreviewOverlay);
            if (ZoomPill.IsVisible && overlayPoint is not null && ZoomPill.Bounds.Contains(overlayPoint.Value))
            {
                return;
            }

            Point? point = e.GetPosition(camera);
            if (point is null)
            {
                return;
            }

            if (!cameraDevices.FocusAt(camera.SelectedCamera.DeviceId, camera, point.Value))
            {
                return;
            }

            hasManualFocusPoint = true;
            await ShowFocusReticleAsync(point.Value);
        }
        catch (Exception ex)
        {
            // A camera that will not focus where it was asked is a small loss next to the app
            // closing itself from an async void.
            logger.LogError(ex, "Failed to focus the camera on a tap.");
        }
    }

    /// <summary>
    /// Marks where the camera was told to focus, then gets out of the way.
    /// </summary>
    private async Task ShowFocusReticleAsync(Point point)
    {
        int generation = ++focusReticleGeneration;

        // An earlier tap's fade would otherwise keep running and take this one's reticle down with
        // it, since setting the opacity back to 1 does not stop an animation already in flight.
        FocusReticle.CancelAnimations();

        // Anchored to the overlay's top left corner, so the tap position only has to be offset by
        // half the reticle to centre it. Scaling happens about the middle and does not move it.
        FocusReticle.TranslationX = point.X - (FocusReticle.WidthRequest / 2);
        FocusReticle.TranslationY = point.Y - (FocusReticle.HeightRequest / 2);
        FocusReticle.Opacity = 1;
        FocusReticle.Scale = 1.3;
        FocusReticle.IsVisible = true;

        await FocusReticle.ScaleToAsync(1, 150, Easing.CubicOut);
        await Task.Delay(700);

        if (generation != focusReticleGeneration)
        {
            return;
        }

        await FocusReticle.FadeToAsync(0, 200);

        if (generation == focusReticleGeneration)
        {
            FocusReticle.IsVisible = false;
        }
    }

    private void HideFocusReticle()
    {
        focusReticleGeneration++;
        FocusReticle.CancelAnimations();
        FocusReticle.IsVisible = false;
    }

    /// <summary>
    /// Zooms the preview as the user pinches it.
    /// </summary>
    /// <remarks>
    /// Every platform reports the pinch as a change since the last update rather than since the
    /// gesture began, so the movement has to be added up here, but they do not all measure it the
    /// same way. Android and Windows send the ratio the fingers moved by, while iOS sends one plus
    /// the change in how far apart they are. Folding either one in the other's way makes the zoom
    /// race ahead of the fingers or lag behind them, which is very obvious on a live preview.
    /// </remarks>
    private void CameraPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                pinchStartZoom = viewModel.ZoomFactor;
                pinchScale = 1;
                break;

            case GestureStatus.Running:
                if (pinchStartZoom <= 0)
                {
                    return;
                }

#if IOS || MACCATALYST
                pinchScale += e.Scale - 1;
#else
                pinchScale *= e.Scale;
#endif

                pinchScale = Math.Max(pinchScale, MinimumPinchScale);
                viewModel.SetZoom((float)(pinchStartZoom * pinchScale));

                // A pinch that runs past either end of the range would otherwise have to be unwound
                // by exactly as much as it overshot before the preview moved again, so the total is
                // pulled back to wherever the camera actually ended up.
                pinchScale = viewModel.ZoomFactor / (double)pinchStartZoom;
                break;
        }
    }

    private async void CameraMediaCaptured(object? sender, MediaCapturedEventArgs e)
    {
        try
        {
            await viewModel.AddCapturedPhotoAsync(e.Media);
        }
        catch (Exception ex)
        {
            // The old implementation threw straight out of this handler when there was no location
            // fix, which crashed the app rather than losing a location.
            logger.LogError(ex, "Failed to handle a captured photo.");
        }
    }

    private async void CaptureClicked(object? sender, EventArgs e)
    {
        if (camera is null)
        {
            return;
        }

        try
        {
            // The camera is put back into continuous autofocus on the way to the shutter, because
            // the toolkit reconfigures the device when it starts the preview and changes the
            // capture format, and neither leaves any promise about what the focus mode ends up as.
            // Somebody who has tapped to focus is left alone: they already said where to look.
            if (!hasManualFocusPoint && camera.SelectedCamera is not null)
            {
                cameraDevices.ResumeContinuousFocus(camera.SelectedCamera.DeviceId);
            }

            await camera.CaptureImage(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to capture a photo.");
            await DisplayAlertAsync("Camera", "Couldn't take that photo.", "OK");
        }
    }

    private void SwitchCameraClicked(object? sender, EventArgs e)
    {
        if (camera is null || selectableCameras.Count < 2)
        {
            return;
        }

        currentCameraIndex = (currentCameraIndex + 1) % selectableCameras.Count;
        camera.SelectedCamera = selectableCameras[currentCameraIndex];
    }

    private void PhotoTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as View)?.BindingContext is PhotoItemViewModel photo)
        {
            viewModel.ToggleSelection(photo);
        }
    }

    private async void ReportClicked(object? sender, EventArgs e)
    {
        List<ReportPhoto> selected = viewModel.SelectedPhotos.Select(item => item.Photo).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            await Shell.Current.GoToAsync(nameof(ReportPage), new Dictionary<string, object>()
            {
                [ReportPage.PhotosParameter] = selected
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open the report page.");
        }
    }

    private async void DeleteClicked(object? sender, EventArgs e)
    {
        try
        {
            int count = viewModel.SelectedPhotos.Count;
            if (count == 0)
            {
                return;
            }

            // Null means every selected photo is one the app took, and iOS puts up its own prompt
            // before deleting those, so asking twice would just be an extra tap.
            string? confirmation = viewModel.BuildDeleteConfirmation();
            if (confirmation is not null)
            {
                string title = count == 1 ? "Delete photo?" : $"Delete {count} photos?";
                if (!await DisplayAlertAsync(title, confirmation, "Delete", "Cancel"))
                {
                    return;
                }
            }

            await viewModel.DeleteSelectedAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete the selected photos.");
            await DisplayAlertAsync("Photos", "Couldn't delete those photos.", "OK");
        }
    }
}
