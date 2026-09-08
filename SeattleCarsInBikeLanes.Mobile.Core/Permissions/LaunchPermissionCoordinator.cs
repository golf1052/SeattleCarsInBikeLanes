namespace SeattleCarsInBikeLanes.Mobile.Core.Permissions;

public enum LaunchPermissionKind
{
    Camera,
    PhotoLibrary,
    Location
}

public enum LaunchPermissionState
{
    NotDetermined,
    Granted,
    Limited,
    Denied,
    Restricted,
    Disabled,
    Failed
}

public sealed record LaunchPermissionResult(
    LaunchPermissionKind Permission,
    LaunchPermissionState State,
    Exception? Error = null);

public sealed record LaunchPermissionSnapshot(
    LaunchPermissionResult Camera,
    LaunchPermissionResult PhotoLibrary,
    LaunchPermissionResult Location);

public interface ILaunchPermissionGateway
{
    Task<LaunchPermissionState> CheckAsync(LaunchPermissionKind permission);

    Task<LaunchPermissionState> RequestAsync(LaunchPermissionKind permission);

    bool WasAutomaticRequestAttempted(LaunchPermissionKind permission);

    void MarkAutomaticRequestAttempted(LaunchPermissionKind permission);
}

/// <summary>
/// Requests launch permissions once, in order, and shares the work with every startup consumer.
/// </summary>
public sealed class LaunchPermissionCoordinator
{
    private readonly object gate = new object();
    private readonly ILaunchPermissionGateway permissions;
    private Task<LaunchPermissionSnapshot>? initialization;

    public LaunchPermissionCoordinator(ILaunchPermissionGateway permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        this.permissions = permissions;
    }

    public Task<LaunchPermissionSnapshot> InitializeAsync()
    {
        lock (gate)
        {
            return initialization ??= InitializeCoreAsync();
        }
    }

    private async Task<LaunchPermissionSnapshot> InitializeCoreAsync()
    {
        LaunchPermissionResult camera = await ResolveAsync(LaunchPermissionKind.Camera);
        LaunchPermissionResult photoLibrary = await ResolveAsync(LaunchPermissionKind.PhotoLibrary);
        LaunchPermissionResult location = await ResolveAsync(LaunchPermissionKind.Location);
        return new LaunchPermissionSnapshot(camera, photoLibrary, location);
    }

    private async Task<LaunchPermissionResult> ResolveAsync(LaunchPermissionKind permission)
    {
        LaunchPermissionState current;
        try
        {
            current = await permissions.CheckAsync(permission);
        }
        catch (Exception ex)
        {
            return new LaunchPermissionResult(permission, LaunchPermissionState.Failed, ex);
        }

        if (current != LaunchPermissionState.NotDetermined)
        {
            return new LaunchPermissionResult(permission, current);
        }

        bool attempted;
        try
        {
            attempted = permissions.WasAutomaticRequestAttempted(permission);
        }
        catch (Exception ex)
        {
            return new LaunchPermissionResult(permission, LaunchPermissionState.Failed, ex);
        }

        if (attempted)
        {
            return new LaunchPermissionResult(permission, LaunchPermissionState.Denied);
        }

        try
        {
            permissions.MarkAutomaticRequestAttempted(permission);
        }
        catch (Exception ex)
        {
            return new LaunchPermissionResult(permission, LaunchPermissionState.Failed, ex);
        }

        try
        {
            LaunchPermissionState requested = await permissions.RequestAsync(permission);
            return new LaunchPermissionResult(permission, requested);
        }
        catch (Exception ex)
        {
            return new LaunchPermissionResult(permission, LaunchPermissionState.Failed, ex);
        }
    }
}
