using AVFoundation;
using CommunityToolkit.Maui.Views;
using Foundation;
using SeattleCarsInBikeLanes.Mobile.Services;
using UIKit;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

public sealed class CameraPreviewReadiness : ICameraPreviewReadiness
{
    public async Task WaitForFirstFrameAsync(CameraView cameraView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cameraView);

        UIView platformView = await GetPlatformViewAsync(cameraView, cancellationToken);
        if (platformView.Layer is not AVCaptureVideoPreviewLayer previewLayer)
        {
            throw new InvalidOperationException("The camera view is not backed by a video preview layer.");
        }

        TaskCompletionSource completion =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable observer = previewLayer.AddObserver(
            "previewing",
            NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New,
            change =>
            {
                if (change.NewValue is NSNumber value && value.BoolValue)
                {
                    completion.TrySetResult();
                }
            });
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        await completion.Task;
    }

    private static async Task<UIView> GetPlatformViewAsync(
        CameraView cameraView,
        CancellationToken cancellationToken)
    {
        if (cameraView.Handler?.PlatformView is UIView current)
        {
            return current;
        }

        TaskCompletionSource<UIView> completion =
            new TaskCompletionSource<UIView>(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandlerChanged(object? sender, EventArgs args)
        {
            if (cameraView.Handler?.PlatformView is UIView platformView)
            {
                completion.TrySetResult(platformView);
            }
        }

        cameraView.HandlerChanged += HandlerChanged;
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            HandlerChanged(cameraView, EventArgs.Empty);
            return await completion.Task;
        }
        finally
        {
            cameraView.HandlerChanged -= HandlerChanged;
        }
    }
}
