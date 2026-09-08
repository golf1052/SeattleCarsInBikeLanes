using SeattleCarsInBikeLanes.Mobile.Core.Permissions;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class LaunchPermissionCoordinatorTests
{
    [Fact]
    public async Task RequestsUndeterminedPermissionsSequentially()
    {
        FakePermissionGateway gateway = new FakePermissionGateway();
        LaunchPermissionCoordinator coordinator = new LaunchPermissionCoordinator(gateway);

        LaunchPermissionSnapshot result = await coordinator.InitializeAsync();

        Assert.Equal(LaunchPermissionState.Granted, result.Camera.State);
        Assert.Equal(LaunchPermissionState.Granted, result.PhotoLibrary.State);
        Assert.Equal(LaunchPermissionState.Granted, result.Location.State);
        Assert.Equal(
            [
                "check:Camera", "mark:Camera", "request:Camera",
                "check:PhotoLibrary", "mark:PhotoLibrary", "request:PhotoLibrary",
                "check:Location", "mark:Location", "request:Location"
            ],
            gateway.Calls);
    }

    [Fact]
    public async Task DoesNotRequestPermissionsThatAreAlreadyResolved()
    {
        FakePermissionGateway gateway = new FakePermissionGateway();
        gateway.CheckedStates[LaunchPermissionKind.Camera] = LaunchPermissionState.Granted;
        gateway.CheckedStates[LaunchPermissionKind.PhotoLibrary] = LaunchPermissionState.Limited;
        gateway.CheckedStates[LaunchPermissionKind.Location] = LaunchPermissionState.Denied;

        LaunchPermissionSnapshot result =
            await new LaunchPermissionCoordinator(gateway).InitializeAsync();

        Assert.Equal(LaunchPermissionState.Granted, result.Camera.State);
        Assert.Equal(LaunchPermissionState.Limited, result.PhotoLibrary.State);
        Assert.Equal(LaunchPermissionState.Denied, result.Location.State);
        Assert.DoesNotContain(gateway.Calls, call => call.StartsWith("request:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotRepeatAnAutomaticRequest()
    {
        FakePermissionGateway gateway = new FakePermissionGateway();
        gateway.Attempted.Add(LaunchPermissionKind.Camera);

        LaunchPermissionSnapshot result =
            await new LaunchPermissionCoordinator(gateway).InitializeAsync();

        Assert.Equal(LaunchPermissionState.Denied, result.Camera.State);
        Assert.DoesNotContain("request:Camera", gateway.Calls);
        Assert.DoesNotContain("mark:Camera", gateway.Calls);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneSequence()
    {
        FakePermissionGateway gateway = new FakePermissionGateway
        {
            RequestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        LaunchPermissionCoordinator coordinator = new LaunchPermissionCoordinator(gateway);

        Task<LaunchPermissionSnapshot> first = coordinator.InitializeAsync();
        Task<LaunchPermissionSnapshot> second = coordinator.InitializeAsync();

        Assert.Same(first, second);
        gateway.RequestGate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(3, gateway.Calls.Count(call => call.StartsWith("request:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ContinuesAfterOnePermissionFails()
    {
        FakePermissionGateway gateway = new FakePermissionGateway();
        gateway.RequestErrors[LaunchPermissionKind.Camera] = new InvalidOperationException("camera failed");

        LaunchPermissionSnapshot result =
            await new LaunchPermissionCoordinator(gateway).InitializeAsync();

        Assert.Equal(LaunchPermissionState.Failed, result.Camera.State);
        Assert.IsType<InvalidOperationException>(result.Camera.Error);
        Assert.Equal(LaunchPermissionState.Granted, result.PhotoLibrary.State);
        Assert.Equal(LaunchPermissionState.Granted, result.Location.State);
    }

    private sealed class FakePermissionGateway : ILaunchPermissionGateway
    {
        public Dictionary<LaunchPermissionKind, LaunchPermissionState> CheckedStates { get; } = new();

        public Dictionary<LaunchPermissionKind, Exception> RequestErrors { get; } = new();

        public HashSet<LaunchPermissionKind> Attempted { get; } = new();

        public List<string> Calls { get; } = new();

        public TaskCompletionSource? RequestGate { get; init; }

        public Task<LaunchPermissionState> CheckAsync(LaunchPermissionKind permission)
        {
            Calls.Add($"check:{permission}");
            return Task.FromResult(
                CheckedStates.GetValueOrDefault(permission, LaunchPermissionState.NotDetermined));
        }

        public async Task<LaunchPermissionState> RequestAsync(LaunchPermissionKind permission)
        {
            Calls.Add($"request:{permission}");
            if (RequestGate is not null)
            {
                await RequestGate.Task;
            }

            if (RequestErrors.TryGetValue(permission, out Exception? error))
            {
                throw error;
            }

            return LaunchPermissionState.Granted;
        }

        public bool WasAutomaticRequestAttempted(LaunchPermissionKind permission) =>
            Attempted.Contains(permission);

        public void MarkAutomaticRequestAttempted(LaunchPermissionKind permission)
        {
            Calls.Add($"mark:{permission}");
            Attempted.Add(permission);
        }
    }
}
