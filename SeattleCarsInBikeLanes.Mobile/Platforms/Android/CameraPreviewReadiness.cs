using AndroidX.Camera.View;
using AndroidX.Lifecycle;
using CommunityToolkit.Maui.Views;
using SeattleCarsInBikeLanes.Mobile.Services;
using Object = Java.Lang.Object;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

public sealed class CameraPreviewReadiness : ICameraPreviewReadiness
{
    public async Task WaitForFirstFrameAsync(CameraView cameraView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cameraView);

        PreviewView previewView = await GetPlatformViewAsync(cameraView, cancellationToken);
        TaskCompletionSource completion =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PreviewObserver observer = new PreviewObserver(() => completion.TrySetResult());

        previewView.PreviewStreamState.ObserveForever(observer);
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            if (previewView.PreviewStreamState.Value is PreviewView.StreamState state &&
                state == PreviewView.StreamState.Streaming)
            {
                completion.TrySetResult();
            }

            await completion.Task;
        }
        finally
        {
            previewView.PreviewStreamState.RemoveObserver(observer);
        }
    }

    private static async Task<PreviewView> GetPlatformViewAsync(
        CameraView cameraView,
        CancellationToken cancellationToken)
    {
        if (cameraView.Handler?.PlatformView is PreviewView current)
        {
            return current;
        }

        TaskCompletionSource<PreviewView> completion =
            new TaskCompletionSource<PreviewView>(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandlerChanged(object? sender, EventArgs args)
        {
            if (cameraView.Handler?.PlatformView is PreviewView platformView)
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

    private sealed class PreviewObserver(Action onStreaming) : Object, IObserver
    {
        public void OnChanged(Object? value)
        {
            if (value is PreviewView.StreamState state &&
                state == PreviewView.StreamState.Streaming)
            {
                onStreaming();
            }
        }
    }
}
