using System.ComponentModel;
using CommunityToolkit.Maui.Views;
using SeattleCarsInBikeLanes.Mobile.Core.Camera;
using SeattleCarsInBikeLanes.Mobile.Services;
using SeattleCarsInBikeLanes.Mobile.ViewModels;

namespace SeattleCarsInBikeLanes.Mobile.Views;

public partial class CameraPage
{
    private readonly CameraCaptureCoordinator captureCoordinator = new CameraCaptureCoordinator();
    private readonly ICameraHardwareControls hardwareControls;
    private Window? hardwareWindow;
    private bool isWindowInteractive = true;
    private bool isCameraSwitching;
    private bool usesNativeZoom;
    private bool synchronizingHardware;

    private bool CanUseCameraPreview => IsPreviewExpected && isWindowInteractive &&
        !isCameraSwitching && viewModel.IsPreviewInteractive && Navigation.ModalStack.Count == 0;

    private void InitializeHardwareControls()
    {
        hardwareControls.CaptureRequested += HardwareCaptureRequested;
        hardwareControls.StateChanged += HardwareStateChanged;
        captureCoordinator.StateChanged += CaptureStateChanged;
        viewModel.PropertyChanged += CameraViewModelPropertyChanged;
    }

    private void ObserveHardwareWindow()
    {
        StopObservingHardwareWindow();
        hardwareWindow = Window;
        isWindowInteractive = true;
        if (hardwareWindow is not null)
        {
            hardwareWindow.Activated += HardwareWindowActivated;
            hardwareWindow.Deactivated += HardwareWindowDeactivated;
        }
    }

    private void StopObservingHardwareWindow()
    {
        if (hardwareWindow is not null)
        {
            hardwareWindow.Activated -= HardwareWindowActivated;
            hardwareWindow.Deactivated -= HardwareWindowDeactivated;
            hardwareWindow = null;
        }
    }

    private void HardwareWindowActivated(object? sender, EventArgs e)
    {
        isWindowInteractive = true;
        hardwareControls.Refresh();
        UpdateHardwareAvailability();
    }

    private void HardwareWindowDeactivated(object? sender, EventArgs e)
    {
        isWindowInteractive = false;
        UpdateHardwareAvailability();
    }

    private void CameraHandlerChanging(object? sender, HandlerChangingEventArgs e)
    {
        // Remove observers and controls before the toolkit disposes the old native session.
        hardwareControls.Detach();
    }

    private void CameraHandlerChanged(object? sender, EventArgs e) => AttachHardwareControls();

    private void AttachHardwareControls()
    {
        if (camera is not null)
        {
            hardwareControls.Attach(camera);
            SynchronizeHardwareState();
            UpdateHardwareAvailability();
        }
    }

    private void CameraViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraViewModel.IsPreviewInteractive))
        {
            hardwareControls.Refresh();
            UpdateHardwareAvailability();
        }
        else if (e.PropertyName == nameof(CameraViewModel.ZoomFactor) && usesNativeZoom && !synchronizingHardware)
        {
            ApplyNativeZoom();
        }
    }

    private void HardwareStateChanged(object? sender, EventArgs e)
    {
        SynchronizeHardwareState();
        UpdateHardwareAvailability();
        ApplyHardwareOverlayAppearance();
    }

    private void SynchronizeHardwareState()
    {
        if (synchronizingHardware || camera?.SelectedCamera is not { } selected)
        {
            return;
        }

        synchronizingHardware = true;
        try
        {
            CameraHardwareStatus status = hardwareControls.Status;
            bool matchesDevice = status.DeviceId == selected.DeviceId;
            ZoomRange? recommendation = matchesDevice ? status.ZoomRange : null;
            bool nativeZoom = recommendation is not null;
            if (nativeZoom && !usesNativeZoom)
            {
                // CameraInfo has cached zoom bounds. Its bindable-property coercion must not
                // override the live system recommendation or write a stale value back to the device.
                camera.RemoveBinding(CameraView.ZoomFactorProperty);
            }

            bool restoreBinding = usesNativeZoom && !nativeZoom;
            usesNativeZoom = nativeZoom;
            viewModel.SetZoomRange(recommendation ??
                ZoomRange.FromCamera(selected.MinimumZoomFactor, selected.MaximumZoomFactor), reset: false);

            if (nativeZoom && status.IsReady && status.ZoomFactor is { } actual)
            {
                viewModel.SetZoom(actual);
                if (viewModel.ZoomFactor != actual)
                {
                    hardwareControls.SetZoom(viewModel.ZoomFactor);
                }
            }

            if (restoreBinding)
            {
                BindTouchZoom();
            }
        }
        finally
        {
            synchronizingHardware = false;
        }
    }

    private void BindTouchZoom() => camera?.SetBinding(CameraView.ZoomFactorProperty,
        new Binding(nameof(CameraViewModel.ZoomFactor), BindingMode.TwoWay));

    private void ApplyNativeZoom()
    {
        if (usesNativeZoom)
        {
            hardwareControls.SetZoom(viewModel.ZoomFactor);
            SynchronizeHardwareState();
        }
    }

    private void UpdateHardwareAvailability()
    {
        bool available = CanUseCameraPreview && !captureCoordinator.IsCapturing;
        hardwareControls.SetEnabled(available);
        CaptureButton.IsEnabled = available;
        SwitchCameraButton.IsEnabled = available && selectableCameras.Count > 1;
    }

    private void CaptureStateChanged(object? sender, EventArgs e) => UpdateHardwareAvailability();

    private async void HardwareCaptureRequested(object? sender, EventArgs e) => await CapturePhotoAsync();

    private void ApplyHardwareOverlayAppearance()
    {
        bool fullscreen = hardwareControls.Status.IsFullscreen && CanUseCameraPreview;
        foreach (View control in new View[] { LatestThumbnailButton, CameraActionButtons, ZoomPill })
        {
            control.Opacity = fullscreen ? 0 : 1;
            control.InputTransparent = fullscreen;
        }

        if (fullscreen)
        {
            HideFocusReticle();
        }
    }
}
