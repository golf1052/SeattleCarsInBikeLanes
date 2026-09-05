using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeattleCarsInBikeLanes.Mobile.Core.Navigation;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.ViewModels;

/// <summary>
/// Account, device and app information.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAuthService authService;
    private readonly IDeviceIdentityService deviceIdentity;
    private readonly IPhotoCatalog photoCatalog;
    private readonly IUploadQueue uploadQueue;
    private readonly WebAuthActionCoordinator webAuthActions;
    private readonly ILogger<SettingsViewModel> logger;

    public SettingsViewModel(IAuthService authService,
        IDeviceIdentityService deviceIdentity,
        IPhotoCatalog photoCatalog,
        IUploadQueue uploadQueue,
        WebAuthActionCoordinator webAuthActions,
        ILogger<SettingsViewModel> logger)
    {
        this.authService = authService;
        this.deviceIdentity = deviceIdentity;
        this.photoCatalog = photoCatalog;
        this.uploadQueue = uploadQueue;
        this.webAuthActions = webAuthActions;
        this.logger = logger;

        authService.IdentityChanged += (_, _) => ApplyIdentity();
        ApplyIdentity();
    }

    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial string? AccountName { get; set; }

    [ObservableProperty]
    public partial string? MastodonName { get; set; }

    [ObservableProperty]
    public partial bool IsBlueskySignedIn { get; set; }

    [ObservableProperty]
    public partial bool IsMastodonSignedIn { get; set; }

    [ObservableProperty]
    public partial string DeviceId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>
    /// Whether reports are credited to the user unless they say otherwise.
    /// </summary>
    public bool AttributeByDefault
    {
        get => AppPreferences.AttributeByDefault;
        set
        {
            AppPreferences.AttributeByDefault = value;
            OnPropertyChanged();
        }
    }

    public string AppVersion => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    [RelayCommand]
    public async Task LoadAsync()
    {
        DeviceId = await deviceIdentity.GetDeviceIdAsync();
        await authService.RefreshAsync();
    }

    [RelayCommand]
    private async Task CopyDeviceIdAsync()
    {
        await Clipboard.Default.SetTextAsync(DeviceId);
        StatusMessage = "Device ID copied.";
    }

    [RelayCommand]
    private async Task SignOutBlueskyAsync()
    {
        try
        {
            await authService.SignOutBlueskyAsync();
            webAuthActions.QueueApplySignedOut(WebAuthProvider.Bluesky);
            StatusMessage = "Signed out of Bluesky.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sign out of Bluesky.");
            StatusMessage = "Couldn't sign out of Bluesky.";
        }
    }

    [RelayCommand]
    private async Task SignOutMastodonAsync()
    {
        try
        {
            await authService.SignOutMastodonAsync();
            webAuthActions.QueueApplySignedOut(WebAuthProvider.Mastodon);
            StatusMessage = "Signed out of Mastodon.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sign out of Mastodon.");
            StatusMessage = "Couldn't sign out of Mastodon.";
        }
    }

    [RelayCommand]
    private async Task ClearImportedPhotosAsync()
    {
        try
        {
            HashSet<string> queued = uploadQueue.Reports
                .SelectMany(report => report.Photos)
                .Select(photo => photo.Id)
                .ToHashSet(StringComparer.Ordinal);
            ForgetImportedPhotosResult result = await photoCatalog.ForgetImportedPhotosAsync(queued);
            StatusMessage = result.Retained == 0
                ? "Imported photos removed from the app."
                : $"{result.Removed} imported photos removed; {result.Retained} kept until queued reports finish.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear imported photos.");
            StatusMessage = "Couldn't remove imported photos.";
        }
    }

    [RelayCommand]
    private static Task OpenWebsiteAsync() => Browser.Default.OpenAsync(SiteUrls.BaseAddress);

    [RelayCommand]
    private static Task OpenPrivacyPolicyAsync() => Browser.Default.OpenAsync(SiteUrls.Privacy);

    [RelayCommand]
    private static Task OpenSourceAsync() =>
        Browser.Default.OpenAsync(new Uri("https://github.com/golf1052/SeattleCarsInBikeLanes"));

    private void ApplyIdentity()
    {
        AttributionIdentity? identity = authService.CurrentIdentity;

        IsSignedIn = identity?.CanAttribute == true;
        AccountName = identity?.BlueskyHandle;
        MastodonName = identity?.MastodonFullUsername;
        IsBlueskySignedIn = !string.IsNullOrWhiteSpace(AccountName);
        IsMastodonSignedIn = !string.IsNullOrWhiteSpace(MastodonName);
    }
}
