using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeattleCarsInBikeLanes.Mobile.Core.Camera;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.ViewModels;

/// <summary>
/// One photo in the roll.
/// </summary>
public sealed partial class PhotoItemViewModel : ObservableObject
{
    public PhotoItemViewModel(ReportPhoto photo)
    {
        Photo = photo;
        Submitted = photo.Submitted;
    }

    public ReportPhoto Photo { get; private set; }

    public string Id => Photo.Id;

    public bool IsImported => Photo.Origin == PhotoOrigin.Imported;

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool Submitted { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void Update(ReportPhoto photo)
    {
        Photo = photo;
        Submitted = photo.Submitted;
    }
}

/// <summary>
/// The camera, which is where the app opens.
/// </summary>
/// <remarks>
/// The whole point of the app is to shorten the path from seeing something to reporting it, so the
/// camera is live on launch and everything else is reachable from here.
/// </remarks>
public sealed partial class CameraViewModel : ObservableObject
{
    private const int ThumbnailPixelSize = 240;

    /// <summary>
    /// Mirrors the roll's layout in CameraPage.xaml, which the reported section's height is worked
    /// out from.
    /// </summary>
    private const int RollColumns = 3;

    private const double ThumbnailHeight = 110;

    private const double ThumbnailSpacing = 4;

    private const double MaxReportedRollHeight = 244;

    private readonly IPhotoCatalog photoCatalog;
    private readonly IPhotoLibraryService photoLibrary;
    private readonly IUploadService uploadService;
    private readonly ICaptureService captureService;
    private readonly ILogger<CameraViewModel> logger;

    public CameraViewModel(IPhotoCatalog photoCatalog,
        IPhotoLibraryService photoLibrary,
        IUploadService uploadService,
        ICaptureService captureService,
        ILogger<CameraViewModel> logger)
    {
        this.photoCatalog = photoCatalog;
        this.photoLibrary = photoLibrary;
        this.uploadService = uploadService;
        this.captureService = captureService;
        this.logger = logger;

        PendingPhotos = new ObservableCollection<PhotoItemViewModel>();
        ReportedPhotos = new ObservableCollection<PhotoItemViewModel>();

        PendingPhotos.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPhotos));
        ReportedPhotos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(HasReportedPhotos));
            OnPropertyChanged(nameof(ReportedHeader));
            OnPropertyChanged(nameof(ReportedRollHeight));
        };
    }

    /// <summary>
    /// The photos that still need reporting.
    /// </summary>
    public ObservableCollection<PhotoItemViewModel> PendingPhotos { get; }

    /// <summary>
    /// The photos that have already been reported.
    /// </summary>
    /// <remarks>
    /// These are kept apart from the rest and folded away by default. Once a photo has been sent it
    /// is history, and leaving it in the roll buries the one or two photos the user actually still
    /// has to do something about.
    /// </remarks>
    public ObservableCollection<PhotoItemViewModel> ReportedPhotos { get; }

    public bool HasPhotos => PendingPhotos.Count > 0 || ReportedPhotos.Count > 0;

    public bool HasReportedPhotos => ReportedPhotos.Count > 0;

    public string ReportedHeader => $"Already reported ({ReportedPhotos.Count})";

    /// <summary>
    /// Whether the already reported photos are folded out.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReportedToggleGlyph))]
    public partial bool AreReportedPhotosExpanded { get; set; }

    /// <summary>
    /// A chevron pointing the way the section will move if tapped.
    /// </summary>
    public string ReportedToggleGlyph => AreReportedPhotosExpanded ? "\uF2B6" : "\uF2A3";

    /// <summary>
    /// How tall the expanded reported section should be.
    /// </summary>
    /// <remarks>
    /// A CollectionView in an auto sized row has nothing to measure against and collapses, so it
    /// has to be told. The height is worked out from the number of rows of thumbnails so a couple
    /// of reported photos do not leave a wall of empty space, and capped so the section can never
    /// crowd out the photos still waiting to be reported.
    /// </remarks>
    public double ReportedRollHeight
    {
        get
        {
            int rows = (ReportedPhotos.Count + RollColumns - 1) / RollColumns;
            double height = (rows * ThumbnailHeight) + (Math.Max(rows - 1, 0) * ThumbnailSpacing);
            return Math.Min(height, MaxReportedRollHeight);
        }
    }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReport))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(IsPreviewInteractive))]
    [NotifyPropertyChangedFor(nameof(CanZoom))]
    public partial bool IsRollVisible { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>
    /// Set when the user has restricted the app to a hand picked set of photos.
    /// </summary>
    /// <remarks>
    /// Under limited access the app's own album is invisible to it, so the roll would silently
    /// appear empty. The user has to be told, or the app just looks broken.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasLimitedPhotoAccess { get; set; }

    /// <summary>
    /// Whether this device has a camera to preview.
    /// </summary>
    /// <remarks>
    /// Without one there is nothing behind the roll, so the roll is opened and left open. The
    /// import path still works, which is the only way to get photos in on such a device.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCamera))]
    [NotifyPropertyChangedFor(nameof(IsPreviewInteractive))]
    [NotifyPropertyChangedFor(nameof(CanZoom))]
    public partial bool HasCamera { get; set; } = true;

    public bool HasNoCamera => !HasCamera;

    partial void OnHasCameraChanged(bool value)
    {
        if (!value)
        {
            IsRollVisible = true;
        }
    }

    /// <summary>
    /// What the camera currently being previewed can be zoomed to.
    /// </summary>
    /// <remarks>
    /// Every lens is different, and a switch to the front camera usually means no zoom at all, so
    /// this is replaced whenever the selected camera changes rather than worked out once.
    /// </remarks>
    public ZoomRange ZoomRange { get; private set; } = ZoomRange.None;

    /// <summary>
    /// Where the camera is zoomed to right now.
    /// </summary>
    /// <remarks>
    /// Bound two way to the preview, so the toolkit forcing this back to 1x whenever a camera
    /// finishes loading is reflected here rather than leaving the label lying about it.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    public partial float ZoomFactor { get; set; } = 1f;

    /// <summary>
    /// Whether there is a live preview for the user to touch.
    /// </summary>
    /// <remarks>
    /// Tapping to focus is worth offering on any camera, including the ones that cannot zoom, so
    /// this is what the layer over the preview follows rather than the narrower
    /// <see cref="CanZoom"/>.
    /// </remarks>
    public bool IsPreviewInteractive => HasCamera && !IsRollVisible;

    /// <summary>
    /// Whether the zoom controls are worth showing.
    /// </summary>
    /// <remarks>
    /// The controls sit over the live preview, so they have to come down when the roll is covering
    /// it, and there is nothing to zoom on a device with no camera or a lens with a fixed field of
    /// view.
    /// </remarks>
    public bool CanZoom => IsPreviewInteractive && ZoomRange.CanZoom;

    /// <summary>
    /// The current zoom, written the way a camera app writes it.
    /// </summary>
    public string ZoomLabel => ZoomRange.Format(ZoomFactor);

    /// <summary>
    /// Takes the zoom range of the camera now being previewed.
    /// </summary>
    /// <remarks>
    /// Zoom goes back to the default because the new lens is a different field of view: holding 5x
    /// across a switch to the front camera would either be silently clamped to nothing or leave the
    /// user staring at a crop they never asked for.
    /// </remarks>
    public void SetZoomRange(float minimum, float maximum)
    {
        ZoomRange = ZoomRange.FromCamera(minimum, maximum);
        OnPropertyChanged(nameof(ZoomRange));
        OnPropertyChanged(nameof(CanZoom));
        ResetZoom();
    }

    /// <summary>
    /// Zooms to a factor, pulling it into what the camera supports.
    /// </summary>
    public void SetZoom(float value) => ZoomFactor = ZoomRange.Clamp(value);

    /// <summary>
    /// Returns the camera to the zoom it should open at.
    /// </summary>
    public void ResetZoom() => ZoomFactor = ZoomRange.Default;

    /// <summary>
    /// Steps to the next zoom stop, wrapping back to the widest.
    /// </summary>
    /// <remarks>
    /// Pinching is fiddly one handed while holding a bike, and most shots want a round number
    /// anyway, so tapping the level cycles the stops the lens can actually reach.
    /// </remarks>
    [RelayCommand]
    private void CycleZoom() => ZoomFactor = ZoomRange.NextPreset(ZoomFactor);

    [ObservableProperty]
    public partial ImageSource? LatestThumbnail { get; set; }

    public int MaxPhotosPerReport => uploadService.Limits.MaxPhotosPerReport;

    public IReadOnlyList<PhotoItemViewModel> SelectedPhotos =>
        PendingPhotos.Concat(ReportedPhotos).Where(photo => photo.IsSelected).ToList();

    /// <summary>
    /// Whether the report button should be offered.
    /// </summary>
    /// <remarks>
    /// A selection outlives closing the roll, so this has to be tied to the roll being up as well.
    /// Otherwise the report button sits over the live camera preview, which is nowhere near the
    /// photos it would be reporting.
    /// </remarks>
    public bool CanReport
    {
        get
        {
            int selected = SelectedPhotos.Count;
            return IsRollVisible && selected > 0 && selected <= MaxPhotosPerReport;
        }
    }

    /// <summary>
    /// Whether the delete button should be offered.
    /// </summary>
    /// <remarks>
    /// Unlike reporting there is no cap, so a user clearing out a pile of old shots is not made to
    /// do it four at a time.
    /// </remarks>
    public bool CanDelete => IsRollVisible && SelectedPhotos.Count > 0;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            PhotoLibraryAccess access = await photoLibrary.RequestAccessAsync();
            HasLimitedPhotoAccess = access == PhotoLibraryAccess.Limited;

            if (access == PhotoLibraryAccess.Denied)
            {
                StatusMessage = "Cars in Bike Lanes needs access to your photos to keep the reports you take.";
                return;
            }

            StatusMessage = null;
            await uploadService.RefreshLimitsAsync();
            await ReloadPhotosAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the photo roll.");
            StatusMessage = "Couldn't load your photos.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Stores a freshly captured photo.
    /// </summary>
    public async Task<PhotoItemViewModel?> AddCapturedPhotoAsync(Stream media)
    {
        ArgumentNullException.ThrowIfNull(media);

        try
        {
            byte[] jpeg = await captureService.PrepareCapturedPhotoAsync(media);

            ReportPhoto? photo = await photoCatalog.AddCapturedPhotoAsync(jpeg);
            if (photo is null)
            {
                StatusMessage = "Couldn't save that photo.";
                return null;
            }

            // A reload can complete while the photo is being saved, in which case it already picked
            // the new asset up and inserting again would put two tiles on screen for one photo.
            if (PendingPhotos.Concat(ReportedPhotos).Any(existing =>
                    string.Equals(existing.Id, photo.Id, StringComparison.Ordinal)))
            {
                return null;
            }

            PhotoItemViewModel item = new PhotoItemViewModel(photo);
            PendingPhotos.Insert(0, item);
            await LoadThumbnailAsync(item);
            LatestThumbnail = item.Thumbnail;
            return item;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to store a captured photo.");
            StatusMessage = "Couldn't save that photo.";
            return null;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        try
        {
            IReadOnlyList<ReportPhoto> imported = await photoCatalog.ImportPhotosAsync(MaxPhotosPerReport);
            if (imported.Count == 0)
            {
                return;
            }

            await ReloadPhotosAsync();
            IsRollVisible = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to import photos.");
            StatusMessage = "Couldn't import those photos.";
        }
    }

    [RelayCommand]
    private void ToggleRoll() => IsRollVisible = !IsRollVisible;

    [RelayCommand]
    private void ToggleReportedPhotos() => AreReportedPhotosExpanded = !AreReportedPhotosExpanded;

    /// <summary>
    /// Adds or removes a photo from the selection.
    /// </summary>
    /// <remarks>
    /// The server refuses reports with more than four photos. Selecting a fifth is still allowed,
    /// because the selection also drives deleting and being made to clear a roll four photos at a
    /// time would be miserable, but the report button goes away and the reason is spelled out.
    /// </remarks>
    public bool ToggleSelection(PhotoItemViewModel photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        photo.IsSelected = !photo.IsSelected;

        StatusMessage = SelectedPhotos.Count > MaxPhotosPerReport
            ? $"A report can have at most {MaxPhotosPerReport} photos. Deselect some to report, or delete them."
            : null;

        OnPropertyChanged(nameof(CanReport));
        OnPropertyChanged(nameof(CanDelete));
        return true;
    }

    /// <summary>
    /// Describes what deleting the selection would do, when the user needs telling.
    /// </summary>
    /// <remarks>
    /// The platform already asks before it destroys photos the app took, so a second confirmation
    /// for those is just an extra tap. Imported photos get no such prompt, and what happens to them
    /// is different enough that it has to be said out loud.
    /// </remarks>
    /// <returns>The message to confirm, or null when the platform's own prompt is enough.</returns>
    public string? BuildDeleteConfirmation()
    {
        IReadOnlyList<PhotoItemViewModel> selected = SelectedPhotos;
        int imported = selected.Count(photo => photo.IsImported);
        if (imported == 0)
        {
            return null;
        }

        int captured = selected.Count - imported;
        string importedPart = imported == 1
            ? "1 imported photo will be removed from Cars in Bike Lanes but kept in your library."
            : $"{imported} imported photos will be removed from Cars in Bike Lanes but kept in your library.";

        if (captured == 0)
        {
            return importedPart;
        }

        string capturedPart = captured == 1
            ? "1 photo taken in the app will be deleted from your library."
            : $"{captured} photos taken in the app will be deleted from your library.";

        return $"{capturedPart} {importedPart}";
    }

    /// <summary>
    /// Deletes the selected photos.
    /// </summary>
    /// <returns>True when anything was actually removed.</returns>
    public async Task<bool> DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PhotoItemViewModel> selected = SelectedPhotos;
        if (selected.Count == 0)
        {
            return false;
        }

        try
        {
            IReadOnlyList<ReportPhoto> removed = await photoCatalog.DeleteAsync(
                selected.Select(item => item.Photo).ToList(),
                cancellationToken);

            if (removed.Count == 0)
            {
                // The user backed out of the platform's prompt, so the roll stays as it was.
                return false;
            }

            HashSet<string> removedIds = removed.Select(photo => photo.Id).ToHashSet(StringComparer.Ordinal);

            // Snapshot first: removing while enumerating the bound collection would be a bug the UI
            // would show as items disappearing at random.
            foreach (PhotoItemViewModel item in selected.Where(item => removedIds.Contains(item.Id)))
            {
                PendingPhotos.Remove(item);
                ReportedPhotos.Remove(item);
            }

            if (LatestThumbnail is not null && !HasPhotos)
            {
                LatestThumbnail = null;
            }
            else
            {
                // The peek button may have been showing one of the photos that just went away.
                RefreshLatestThumbnail();
            }

            StatusMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete the selected photos.");
            StatusMessage = "Couldn't delete those photos.";
            return false;
        }
        finally
        {
            OnPropertyChanged(nameof(CanReport));
            OnPropertyChanged(nameof(CanDelete));
        }
    }

    /// <summary>
    /// Refreshes the roll from the photo library.
    /// </summary>
    /// <remarks>
    /// The selection is deliberately dropped. This runs whenever the tab is returned to, including
    /// straight after a report was sent, and keeping the old selection would leave the just
    /// submitted photos ticked with the report button live, one tap away from a duplicate.
    /// </remarks>
    public async Task ReloadPhotosAsync()
    {
        IReadOnlyList<ReportPhoto> photos = await photoCatalog.GetPhotosAsync();

        // Existing items are reused so their loaded thumbnails survive the refresh. A duplicate id
        // should not be possible, but ToDictionary would throw for the rest of the session if one
        // ever appeared, leaving the roll stuck on stale tiles.
        Dictionary<string, PhotoItemViewModel> existing = PendingPhotos
            .Concat(ReportedPhotos)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        PendingPhotos.Clear();
        ReportedPhotos.Clear();

        // Notified straight away so the report button hides for the duration of the reload rather
        // than sitting there enabled against a selection that no longer exists.
        OnPropertyChanged(nameof(CanReport));
        OnPropertyChanged(nameof(CanDelete));

        foreach (ReportPhoto photo in photos)
        {
            if (existing.TryGetValue(photo.Id, out PhotoItemViewModel? item))
            {
                item.Update(photo);
            }
            else
            {
                item = new PhotoItemViewModel(photo);
            }

            item.IsSelected = false;

            if (photo.Submitted)
            {
                ReportedPhotos.Add(item);
            }
            else
            {
                PendingPhotos.Add(item);
            }
        }

        // Nothing left to fold away, so the section should not come back open next time there is.
        if (ReportedPhotos.Count == 0)
        {
            AreReportedPhotosExpanded = false;
        }

        // Snapshot before awaiting: a capture finishing mid reload would otherwise mutate one of the
        // bound collections while this is still walking it.
        List<PhotoItemViewModel> loaded = PendingPhotos.Concat(ReportedPhotos).ToList();
        foreach (PhotoItemViewModel item in loaded)
        {
            await LoadThumbnailAsync(item);
        }

        RefreshLatestThumbnail();

        OnPropertyChanged(nameof(CanReport));
        OnPropertyChanged(nameof(CanDelete));
    }

    /// <summary>
    /// Points the peek button at the newest photo the app knows about.
    /// </summary>
    /// <remarks>
    /// The roll is split in two and each half is newest first, so the newest photo overall can be
    /// at the top of either one.
    /// </remarks>
    private void RefreshLatestThumbnail()
    {
        PhotoItemViewModel? newest = null;

        foreach (PhotoItemViewModel item in PendingPhotos.Concat(ReportedPhotos))
        {
            if (newest is null ||
                (item.Photo.CreatedAt ?? DateTimeOffset.MinValue) > (newest.Photo.CreatedAt ?? DateTimeOffset.MinValue))
            {
                newest = item;
            }
        }

        LatestThumbnail = newest?.Thumbnail;
    }

    private async Task LoadThumbnailAsync(PhotoItemViewModel item)
    {
        if (item.Thumbnail is not null)
        {
            return;
        }

        byte[]? jpeg = await photoLibrary.GetThumbnailAsync(item.Id, ThumbnailPixelSize);
        if (jpeg is not null)
        {
            item.Thumbnail = ImageSource.FromStream(() => new MemoryStream(jpeg));
        }
    }
}
