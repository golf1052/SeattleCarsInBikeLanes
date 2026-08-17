using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly IImportedPhotoStore importedPhotos;
    private readonly IPhotoLibraryService photoLibrary;
    private readonly IUploadQueue uploadQueue;
    private readonly ILogger<SettingsViewModel> logger;

    public SettingsViewModel(IAuthService authService,
        IDeviceIdentityService deviceIdentity,
        IImportedPhotoStore importedPhotos,
    IPhotoLibraryService photoLibrary,
    IUploadQueue uploadQueue,
    ILogger<SettingsViewModel> logger)
    {
        this.authService = authService;
        this.deviceIdentity = deviceIdentity;
        this.importedPhotos = importedPhotos;
        this.photoLibrary = photoLibrary;
        this.uploadQueue = uploadQueue;
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
    private async Task SignOutAsync()
    {
        try
        {
            await authService.SignOutAsync();
            StatusMessage = "Signed out.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sign out.");
            StatusMessage = "Couldn't sign out.";
        }
    }

    [RelayCommand]
    private async Task ClearImportedPhotosAsync()
    {
        IReadOnlyList<ImportedPhoto> imported = await importedPhotos.GetAllAsync();
        HashSet<string> queued = uploadQueue.Reports
            .SelectMany(report => report.Photos)
            .Select(photo => photo.Id)
            .ToHashSet(StringComparer.Ordinal);
        List<string> removable = imported
            .Select(photo => photo.LocalIdentifier)
            .Where(id => !queued.Contains(id))
            .ToList();

        await importedPhotos.RemoveAsync(removable);
        await photoLibrary.ReleasePhotoAccessAsync(removable);

        int retained = imported.Count - removable.Count;
        StatusMessage = retained == 0
            ? "Imported photos removed from the app."
            : $"{removable.Count} imported photos removed; {retained} kept until queued reports finish.";
    }

    [RelayCommand]
    private static Task OpenWebsiteAsync() => Browser.Default.OpenAsync(SiteUrls.BaseAddress);

    [RelayCommand]
    private static Task OpenSourceAsync() =>
        Browser.Default.OpenAsync(new Uri("https://github.com/golf1052/SeattleCarsInBikeLanes"));

    private void ApplyIdentity()
    {
        AttributionIdentity? identity = authService.CurrentIdentity;

        IsSignedIn = identity?.CanAttribute == true;
        AccountName = identity?.BlueskyHandle;
        MastodonName = identity?.MastodonFullUsername;
    }
}
