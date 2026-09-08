using SeattleCarsInBikeLanes.Mobile.Core.Permissions;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Adapts the platform permission APIs to the launch-time permission coordinator.
/// </summary>
public sealed class MauiLaunchPermissionGateway : ILaunchPermissionGateway
{
    private readonly IPhotoLibraryService photoLibrary;
    private readonly ICameraReadinessMetrics cameraReadiness;

    public MauiLaunchPermissionGateway(
        IPhotoLibraryService photoLibrary,
        ICameraReadinessMetrics cameraReadiness)
    {
        this.photoLibrary = photoLibrary;
        this.cameraReadiness = cameraReadiness;
    }

    public async Task<LaunchPermissionState> CheckAsync(LaunchPermissionKind permission) =>
        permission switch
        {
            LaunchPermissionKind.Camera => MapRuntimePermission(
                await Permissions.CheckStatusAsync<Permissions.Camera>(),
                AppPreferences.CameraPermissionRequested),
            LaunchPermissionKind.PhotoLibrary => MapPhotoLibraryAccess(
                await photoLibrary.CheckAccessAsync()),
            LaunchPermissionKind.Location => MapRuntimePermission(
                await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>(),
                AppPreferences.LocationPermissionRequested),
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null)
        };

    public async Task<LaunchPermissionState> RequestAsync(LaunchPermissionKind permission) =>
        permission switch
        {
            LaunchPermissionKind.Camera =>
                await MainThread.InvokeOnMainThreadAsync(RequestCameraAsync),
            LaunchPermissionKind.PhotoLibrary =>
                await RequestPhotoLibraryAsync(),
            LaunchPermissionKind.Location =>
                await RequestLocationAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null)
        };

    public bool WasAutomaticRequestAttempted(LaunchPermissionKind permission) =>
        permission switch
        {
            LaunchPermissionKind.Camera => AppPreferences.CameraPermissionRequested,
            LaunchPermissionKind.PhotoLibrary => AppPreferences.PhotoLibraryPermissionRequested,
            LaunchPermissionKind.Location => AppPreferences.LocationPermissionRequested,
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null)
        };

    public void MarkAutomaticRequestAttempted(LaunchPermissionKind permission)
    {
        switch (permission)
        {
            case LaunchPermissionKind.Camera:
                AppPreferences.CameraPermissionRequested = true;
                break;
            case LaunchPermissionKind.PhotoLibrary:
                AppPreferences.PhotoLibraryPermissionRequested = true;
                break;
            case LaunchPermissionKind.Location:
                AppPreferences.LocationPermissionRequested = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(permission), permission, null);
        }
    }

    private async Task<LaunchPermissionState> RequestCameraAsync() =>
        await cameraReadiness.RequestCameraPermissionAsync()
            ? LaunchPermissionState.Granted
            : LaunchPermissionState.Denied;

    private async Task<LaunchPermissionState> RequestPhotoLibraryAsync()
    {
        using IDisposable? excludedDelay = cameraReadiness.ExcludeLaunchPermissionPrompt();
        PhotoLibraryAccess access = await MainThread.InvokeOnMainThreadAsync(
            () => photoLibrary.RequestAccessAsync());
        return MapPhotoLibraryAccess(access);
    }

    private async Task<LaunchPermissionState> RequestLocationAsync()
    {
        using IDisposable? excludedDelay = cameraReadiness.ExcludeLaunchPermissionPrompt();
        PermissionStatus status = await MainThread.InvokeOnMainThreadAsync(
            () => Permissions.RequestAsync<Permissions.LocationWhenInUse>());
        return MapRequestedRuntimePermission(status);
    }

    private static LaunchPermissionState MapRuntimePermission(
        PermissionStatus status,
        bool automaticRequestAttempted) =>
        status switch
        {
            PermissionStatus.Granted => LaunchPermissionState.Granted,
            PermissionStatus.Limited => LaunchPermissionState.Limited,
            PermissionStatus.Restricted => LaunchPermissionState.Restricted,
            PermissionStatus.Disabled => LaunchPermissionState.Disabled,
            PermissionStatus.Unknown => LaunchPermissionState.NotDetermined,
            PermissionStatus.Denied when
                DeviceInfo.Current.Platform == DevicePlatform.Android &&
                !automaticRequestAttempted => LaunchPermissionState.NotDetermined,
            PermissionStatus.Denied => LaunchPermissionState.Denied,
            _ => LaunchPermissionState.Denied
        };

    private static LaunchPermissionState MapRequestedRuntimePermission(PermissionStatus status) =>
        status switch
        {
            PermissionStatus.Granted => LaunchPermissionState.Granted,
            PermissionStatus.Limited => LaunchPermissionState.Limited,
            PermissionStatus.Restricted => LaunchPermissionState.Restricted,
            PermissionStatus.Disabled => LaunchPermissionState.Disabled,
            _ => LaunchPermissionState.Denied
        };

    private static LaunchPermissionState MapPhotoLibraryAccess(PhotoLibraryAccess access) =>
        access switch
        {
            PhotoLibraryAccess.NotDetermined => LaunchPermissionState.NotDetermined,
            PhotoLibraryAccess.Granted => LaunchPermissionState.Granted,
            PhotoLibraryAccess.Limited => LaunchPermissionState.Limited,
            PhotoLibraryAccess.Denied => LaunchPermissionState.Denied,
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, null)
        };
}
