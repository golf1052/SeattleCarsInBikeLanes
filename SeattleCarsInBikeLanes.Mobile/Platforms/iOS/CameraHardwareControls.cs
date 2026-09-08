using System.Runtime.Versioning;
using AVFoundation;
using AVKit;
using CommunityToolkit.Maui.Views;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using ObjCRuntime;
using SeattleCarsInBikeLanes.Mobile.Core.Camera;
using SeattleCarsInBikeLanes.Mobile.Services;
using UIKit;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

[SupportedOSPlatform("ios18.0")]
public sealed class CameraHardwareControls(ILogger<CameraHardwareControls> logger) : ICameraHardwareControls
{
    private readonly CameraHardwareInput input = new CameraHardwareInput();
    private readonly List<IDisposable> sessionObservers = [];
    private readonly List<IDisposable> deviceObservers = [];
    private CameraView? camera;
    private UIView? platformView;
    private AVCaptureVideoPreviewLayer? previewLayer;
    private AVCaptureSession? session;
    private AVCaptureDevice? device;
    private AVCaptureEventInteraction? interaction;
    private AVCaptureSlider? zoomSlider;
    private CameraZoomSynchronizer? zoomSynchronization;
    private bool zoomSliderRegistered;
    private ControlsDelegate? controlsDelegate;
    private bool requestedEnabled;
    private bool isFullscreen;
    private int attachmentGeneration;
    private (NativeHandle Format, ZoomRange Range)? attemptedZoomConfiguration;
    private int zoomSliderGeneration;

    public event EventHandler? CaptureRequested;
    public event EventHandler? StateChanged;

    public CameraHardwareStatus Status { get; private set; }

    public void Attach(CameraView camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (ReferenceEquals(this.camera, camera) &&
            ReferenceEquals(platformView, camera.Handler?.PlatformView))
        {
            Refresh();
            return;
        }

        Detach();
        if (camera.Handler?.PlatformView is not UIView view ||
            view.Layer is not AVCaptureVideoPreviewLayer layer ||
            layer.Session is not { SupportsControls: true } captureSession)
        {
            return;
        }

        this.camera = camera;
        platformView = view;
        previewLayer = layer;
        session = captureSession;
        int generation = attachmentGeneration;

        sessionObservers.Add(captureSession.AddObserver("running", NSKeyValueObservingOptions.New,
            _ => ScheduleRefresh(generation)));
        sessionObservers.Add(layer.AddObserver("previewing", NSKeyValueObservingOptions.New,
            _ => ScheduleRefresh(generation)));
        sessionObservers.Add(AVCaptureSession.Notifications.ObserveWasInterrupted(captureSession,
            (_, _) => ScheduleRefresh(generation)));
        sessionObservers.Add(AVCaptureSession.Notifications.ObserveInterruptionEnded(captureSession,
            (_, _) => ScheduleRefresh(generation)));
        sessionObservers.Add(AVCaptureSession.Notifications.ObserveRuntimeError(captureSession, (_, args) =>
        {
            logger.LogWarning("Camera Control session error: {Error}", args.Error?.LocalizedDescription);
            ScheduleRefresh(generation);
        }));
        Refresh();
    }

    public void Refresh()
    {
        if (session is null)
        {
            return;
        }

        try
        {
            AVCaptureDevice? activeDevice = session.Inputs.OfType<AVCaptureDeviceInput>()
                .Select(value => value.Device)
                .FirstOrDefault(value => value.UniqueID == camera?.SelectedCamera?.DeviceId);

            if (activeDevice is not null && activeDevice.UniqueID != device?.UniqueID)
            {
                BindDevice(activeDevice);
            }

            ConfigureZoomSlider();
            PublishState();
        }
        catch (Exception ex) when (ex is ObjCException or NSErrorException or InvalidOperationException)
        {
            logger.LogError(ex, "Could not configure in-app Camera Control.");
            Detach();
        }
    }

    private void BindDevice(AVCaptureDevice activeDevice)
    {
        RemoveDeviceControls();
        device = activeDevice;
        int generation = input.Generation;
        interaction = new AVCaptureEventInteraction(captureEvent =>
        {
            PublishState();
            CameraShutterPhase phase = captureEvent.Phase switch
            {
                AVCaptureEventPhase.Began => CameraShutterPhase.Began,
                AVCaptureEventPhase.Ended => CameraShutterPhase.Ended,
                _ => CameraShutterPhase.Cancelled
            };
            if (input.Handle(generation, phase))
            {
                CaptureRequested?.Invoke(this, EventArgs.Empty);
            }
        })
        {
            Enabled = false
        };
        platformView!.AddInteraction(interaction);

        controlsDelegate = new ControlsDelegate(fullscreen =>
        {
            if (input.IsCurrent(generation))
            {
                isFullscreen = fullscreen && input.IsEnabled;
                PublishState();
            }
        });
        session!.SetControlsDelegate(controlsDelegate, DispatchQueue.MainQueue);

        foreach (string key in new[] { "activeFormat", "videoZoomFactor",
            "minAvailableVideoZoomFactor", "maxAvailableVideoZoomFactor" })
        {
            deviceObservers.Add(activeDevice.AddObserver(key, NSKeyValueObservingOptions.New,
                _ => ScheduleRefresh(attachmentGeneration, generation)));
        }
    }

    private ZoomRange? ReadRecommendedRange()
    {
        if (device?.ActiveFormat.SystemRecommendedVideoZoomRange is not { } recommendation)
        {
            return null;
        }

        return ZoomRange.FromSystemRecommendation(
            (float)recommendation.MinZoomFactor, (float)recommendation.MaxZoomFactor,
            (float)device.MinAvailableVideoZoomFactor, (float)device.MaxAvailableVideoZoomFactor);
    }

    private void ConfigureZoomSlider()
    {
        if (session is null || device is null)
        {
            return;
        }

        ZoomRange? recommendation = ReadRecommendedRange();
        if (zoomSlider is not null && zoomSynchronization?.Range == recommendation)
        {
            return;
        }

        if (zoomSlider is not null)
        {
            RemoveZoomSlider();
            attemptedZoomConfiguration = null;
        }
        if (recommendation is not { CanZoom: true } range)
        {
            attemptedZoomConfiguration = null;
            return;
        }

        var configuration = (device.ActiveFormat.Handle, range);
        if (attemptedZoomConfiguration == configuration)
        {
            return;
        }
        attemptedZoomConfiguration = configuration;

        CameraZoomSynchronizer synchronization = new CameraZoomSynchronizer(range);
        AVCaptureSlider slider = new AVCaptureSlider(
            "Zoom", "magnifyingglass", range.Minimum, range.Maximum, synchronization.SliderStep)
        {
            LocalizedValueFormat = "%@x"
        };
        int generation = ++zoomSliderGeneration;
        slider.SetActionQueue(DispatchQueue.MainQueue, value => ZoomSliderChanged(generation, value));
        slider.Value = range.Clamp((float)device.VideoZoomFactor);
        if (!session.CanAddControl(slider))
        {
            slider.Dispose();
            logger.LogWarning("The capture session cannot add its Camera Control zoom slider.");
            return;
        }

        zoomSlider = slider;
        zoomSynchronization = synchronization;
    }

    private void UpdateZoomSliderRegistration(bool register)
    {
        if (session is null || zoomSlider is null || register == zoomSliderRegistered)
        {
            return;
        }

        int generation = ++zoomSliderGeneration;
        if (register && !session.CanAddControl(zoomSlider))
        {
            logger.LogWarning("The capture session cannot restore its Camera Control zoom slider.");
            return;
        }

        // iOS can leave the overlay disabled after toggling AVCaptureSlider.Enabled back to true.
        // Remove inactive controls from the session instead, and register them already enabled.
        // This also invalidates queued actions from before navigation or suspension.
        session.BeginConfiguration();
        try
        {
            if (register)
            {
                zoomSlider.SetActionQueue(DispatchQueue.MainQueue, value => ZoomSliderChanged(generation, value));
                SynchronizeZoomSlider(force: true);
                session.AddControl(zoomSlider);
            }
            else
            {
                session.RemoveControl(zoomSlider);
            }
            zoomSliderRegistered = register;
        }
        finally
        {
            session.CommitConfiguration();
        }
    }

    private void ZoomSliderChanged(int generation, float value)
    {
        if (generation != zoomSliderGeneration || zoomSlider is null || zoomSynchronization is null ||
            !zoomSliderRegistered || !input.IsEnabled || !IsNativePreviewReady() || device is null)
        {
            return;
        }

        if (zoomSynchronization.ReadSliderChange(value, zoomSlider.Value, (float)device.VideoZoomFactor)
            is { } target)
        {
            SetZoomCore(target, fromSlider: true);
        }
    }

    private void SynchronizeZoomSlider(bool force = false)
    {
        if (zoomSlider is not null && zoomSynchronization is not null && device is not null &&
            zoomSynchronization.ReadDeviceChange((float)device.VideoZoomFactor, zoomSlider.Value, force)
                is { } value)
        {
            zoomSlider.Value = value;
        }
    }

    private bool IsNativePreviewReady()
    {
        if (platformView?.Window is null || session is not { Running: true, Interrupted: false } ||
            previewLayer?.Previewing != true || device is null ||
            device.UniqueID != camera?.SelectedCamera?.DeviceId ||
            UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active)
        {
            return false;
        }

        for (UIResponder? responder = platformView; responder is not null; responder = responder.NextResponder)
        {
            if (responder is UIViewController { PresentedViewController: not null })
            {
                return false;
            }
        }

        return true;
    }

    private void PublishState()
    {
        bool ready = IsNativePreviewReady();
        bool enabled = requestedEnabled && ready;
        input.SetEnabled(enabled);
        if (interaction is not null)
        {
            interaction.Enabled = enabled;
        }

        ZoomRange? range = zoomSlider is null ? null : ReadRecommendedRange();
        if (zoomSlider is not null)
        {
            bool zoomEnabled = enabled && range is { CanZoom: true } && zoomSynchronization?.Range == range;
            bool wasEnabled = zoomSliderRegistered;
            UpdateZoomSliderRegistration(zoomEnabled);
            SynchronizeZoomSlider(force: wasEnabled && !zoomEnabled);
        }

        if (!enabled)
        {
            isFullscreen = false;
        }

        CameraHardwareStatus next = new CameraHardwareStatus(
            device?.UniqueID, ready, range, device is null ? null : (float)device.VideoZoomFactor, isFullscreen);
        if (Status != next)
        {
            Status = next;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetEnabled(bool enabled)
    {
        requestedEnabled = enabled;
        PublishState();
    }

    public void SetZoom(float factor) => SetZoomCore(factor, fromSlider: false);

    private void SetZoomCore(float factor, bool fromSlider)
    {
        if (!IsNativePreviewReady() || device is null || zoomSlider is null ||
            ReadRecommendedRange() is not { } range)
        {
            return;
        }

        float target = range.Clamp(factor);
        if (Math.Abs((float)device.VideoZoomFactor - target) < 0.0001f)
        {
            SynchronizeZoomSlider(force: !fromSlider);
            return;
        }

        if (!device.LockForConfiguration(out NSError? error))
        {
            logger.LogWarning("Could not lock the camera to change zoom: {Error}", error?.LocalizedDescription);
            SynchronizeZoomSlider(force: true);
            PublishState();
            return;
        }

        try
        {
            // Availability can narrow when native controls become active. Recheck while holding
            // the device lock rather than writing a value from an earlier observer notification.
            device.VideoZoomFactor = Math.Clamp(target,
                (float)device.MinAvailableVideoZoomFactor, (float)device.MaxAvailableVideoZoomFactor);
        }
        finally
        {
            device.UnlockForConfiguration();
        }

        if (fromSlider && Math.Abs((float)device.VideoZoomFactor - target) < 0.0001f)
        {
            // A later swipe may already have updated Value while its action is queued.
            // Acknowledge this device write without pushing the earlier value back over it.
            zoomSynchronization?.AcceptSliderChange((float)device.VideoZoomFactor);
        }
        else
        {
            SynchronizeZoomSlider(force: true);
        }
        PublishState();
    }

    private void ScheduleRefresh(int attachment, int? deviceGeneration = null)
    {
        // Always enqueue, including on the UI thread: a device KVO callback can run while the
        // toolkit holds its configuration lock. Read current values, not an old callback's payload.
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (attachment == attachmentGeneration &&
                (deviceGeneration is null || input.IsCurrent(deviceGeneration.Value)))
            {
                Refresh();
            }
        });
    }

    private void RemoveDeviceControls()
    {
        input.Reset();
        isFullscreen = false;
        attemptedZoomConfiguration = null;
        foreach (IDisposable observer in deviceObservers)
        {
            observer.Dispose();
        }
        deviceObservers.Clear();

        if (interaction is not null)
        {
            interaction.Enabled = false;
            platformView?.RemoveInteraction(interaction);
            interaction.Dispose();
            interaction = null;
        }

        if (session is not null)
        {
            RemoveZoomSlider();

            if (ReferenceEquals(session.ControlsDelegate, controlsDelegate))
            {
                session.SetControlsDelegate(null, null);
            }
        }

        controlsDelegate?.Dispose();
        controlsDelegate = null;
        device = null;
    }

    private void RemoveZoomSlider()
    {
        zoomSliderGeneration++;
        if (session is null || zoomSlider is null)
        {
            return;
        }

        AVCaptureSlider slider = zoomSlider;
        UpdateZoomSliderRegistration(false);
        zoomSlider = null;
        zoomSynchronization = null;
        slider.Dispose();
    }

    public void Detach()
    {
        attachmentGeneration++;
        requestedEnabled = false;
        foreach (IDisposable observer in sessionObservers)
        {
            observer.Dispose();
        }
        sessionObservers.Clear();
        RemoveDeviceControls();
        camera = null;
        platformView = null;
        previewLayer = null;
        session = null;
        PublishState();
    }

    private sealed class ControlsDelegate(Action<bool> fullscreenChanged)
        : NSObject, IAVCaptureSessionControlsDelegate
    {
        [Export("sessionControlsDidBecomeActive:")]
        public void DidBecomeActive(AVCaptureSession session) { }

        [Export("sessionControlsWillEnterFullscreenAppearance:")]
        public void WillEnterFullscreenAppearance(AVCaptureSession session) => fullscreenChanged(true);

        [Export("sessionControlsWillExitFullscreenAppearance:")]
        public void WillExitFullscreenAppearance(AVCaptureSession session) => fullscreenChanged(false);

        [Export("sessionControlsDidBecomeInactive:")]
        public void DidBecomeInactive(AVCaptureSession session) => fullscreenChanged(false);
    }
}
