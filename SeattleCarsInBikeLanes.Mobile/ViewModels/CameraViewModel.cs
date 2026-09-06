using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeattleCarsInBikeLanes.Mobile.Core.Camera;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.ViewModels;

/// <summary>
/// One photo in the roll.
/// </summary>
public sealed partial class PhotoItemViewModel : ObservableObject, IReportedPhoto
{
    public PhotoItemViewModel(ReportPhoto photo)
    {
        Photo = photo;
        Submitted = photo.Submitted;
    }

    public ReportPhoto Photo { get; private set; }

    public string Id => Photo.Id;

    public DateTimeOffset? CreatedAt => Photo.CreatedAt;

    public string? SubmissionId => Photo.SubmissionId;

    public DateTimeOffset? SubmittedAt => Photo.SubmittedAt;

    public bool IsImported =>
        Photo.Origin is PhotoOrigin.Imported or PhotoOrigin.PrivateImported;

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool Submitted { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// Where the photo's report has got to, when there is one waiting to be sent.
    /// </summary>
    /// <remarks>
    /// A photo whose report is still in the queue has not reached the site and does not carry the
    /// submitted flag, so without this it would sit in the roll looking untouched and invite the
    /// user to report it a second time.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    [NotifyPropertyChangedFor(nameof(QueueBadge))]
    [NotifyPropertyChangedFor(nameof(QueueBadgeColor))]
    public partial UploadQueueState? QueueState { get; set; }

    /// <summary>
    /// Whether the photo is already spoken for by a report on its way out.
    /// </summary>
    public bool IsQueued => QueueState is UploadQueueState.Pending or UploadQueueState.Uploading;

    /// <summary>
    /// The short label the tile carries while its report is on its way.
    /// </summary>
    public string? QueueBadge => QueueState switch
    {
        UploadQueueState.Uploading => "Sending",
        UploadQueueState.Pending => "Queued",
        UploadQueueState.Failed => "Failed",
        _ => null
    };

    public Color QueueBadgeColor => QueueState == UploadQueueState.Failed
        ? Color.FromArgb("#CC7A1F1F")
        : Color.FromArgb("#CC1F4E7A");

    public void Update(ReportPhoto photo)
    {
        Photo = photo;
        Submitted = photo.Submitted;
    }
}

/// <summary>
/// A report the app gave up on, and what the user can do about it.
/// </summary>
/// <remarks>
/// The row carries its own commands rather than reaching back up to the page's view model with a
/// RelativeSource binding. An ancestor binding that does not resolve fails silently, which here
/// would mean a Retry button that quietly does nothing, and nothing in this project validates XAML
/// at build time to catch that.
/// </remarks>
public sealed partial class FailedReportViewModel : ObservableObject
{
    private readonly IUploadQueue uploadQueue;

    public FailedReportViewModel(QueuedReport report, IUploadQueue uploadQueue)
    {
        this.uploadQueue = uploadQueue;

        Id = report.Id;
        Title = $"Report of {report.Description} wasn't sent";
        Error = report.LastError ?? "The report couldn't be sent.";
    }

    public string Id { get; }

    /// <summary>
    /// What the row is about.
    /// </summary>
    /// <remarks>
    /// Built here rather than with a StringFormat in the markup. An apostrophe cannot be escaped
    /// inside a markup extension's quoted string, and getting that wrong is a parse error the build
    /// does not catch: the template is only read when the page is first shown.
    /// </remarks>
    public string Title { get; }

    /// <summary>
    /// Why it failed, in the server's own words where it gave any.
    /// </summary>
    /// <remarks>
    /// The site explains itself in language written for users, and repeating it is far more use
    /// than a generic apology: "Photo not taken in Seattle" tells somebody exactly what happened.
    /// </remarks>
    public string Error { get; }

    /// <summary>
    /// Puts the report back in the queue.
    /// </summary>
    [RelayCommand]
    private async Task RetryAsync() => await uploadQueue.RetryAsync(Id);

    /// <summary>
    /// Throws the report away.
    /// </summary>
    /// <remarks>
    /// Its photos go back to being unreported rather than disappearing, because the user may well
    /// want to fix whatever the site objected to and try again.
    /// </remarks>
    [RelayCommand]
    private async Task DiscardAsync() => await uploadQueue.DiscardAsync(Id);
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

    private readonly IPhotoCatalog photoCatalog;
    private readonly IPhotoLibraryService photoLibrary;
    private readonly IUploadService uploadService;
    private readonly IUploadQueue uploadQueue;
    private readonly ICaptureService captureService;
    private readonly ILogger<CameraViewModel> logger;

    /// <summary>
    /// Photos the user has taken out of an automatic selection, which are never put back into one.
    /// </summary>
    /// <remarks>
    /// Deselecting is the user saying the app guessed wrong. Guessing the same way again the next
    /// time they open the roll would be the app arguing with them, and the roll is reloaded often
    /// enough that it would happen constantly. Kept for the life of the view model rather than
    /// written down, because it is about this ride rather than about the photo.
    /// </remarks>
    private readonly HashSet<string> deselectedPhotoIds = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The photos the last automatic selection ticked.
    /// </summary>
    /// <remarks>
    /// Kept so a later suggestion can take back the earlier one. Without this, a photo taken while
    /// the roll was already open would arrive after something older had been suggested, and the
    /// guard against overwriting a selection would leave the user one tap from reporting the wrong
    /// photo.
    /// </remarks>
    private readonly HashSet<string> autoSelectedPhotoIds = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Set once the user has touched the selection themselves.
    /// </summary>
    /// <remarks>
    /// From that point the selection belongs to them and nothing here changes it, until the roll is
    /// reloaded and the selection is dropped anyway.
    /// </remarks>
    private bool selectionAdjustedByUser;

    public CameraViewModel(IPhotoCatalog photoCatalog,
        IPhotoLibraryService photoLibrary,
        IUploadService uploadService,
        IUploadQueue uploadQueue,
        ICaptureService captureService,
        ILogger<CameraViewModel> logger)
    {
        this.photoCatalog = photoCatalog;
        this.photoLibrary = photoLibrary;
        this.uploadService = uploadService;
        this.uploadQueue = uploadQueue;
        this.captureService = captureService;
        this.logger = logger;

        PendingPhotos = new ObservableCollection<PhotoItemViewModel>();
        RecentPhotos = new ObservableCollection<PhotoItemViewModel>();
        ReportedPhotos = new ObservableCollection<PhotoItemViewModel>();
        FailedReports = new ObservableCollection<FailedReportViewModel>();

        PendingPhotos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(HasEarlierPhotos));
        };

        RecentPhotos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(HasRecentPhotos));
            OnPropertyChanged(nameof(HasEarlierPhotos));
            OnPropertyChanged(nameof(RecentHeader));
            OnPropertyChanged(nameof(RecentRollHeight));

            // The two pinned sections share a height budget, so one growing takes from the other.
            OnPropertyChanged(nameof(ReportedRollHeight));
        };

        ReportedPhotos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(HasReportedPhotos));
            OnPropertyChanged(nameof(ReportedHeader));
            OnPropertyChanged(nameof(ReportedRollHeight));

            // The two pinned sections share a height budget, so one appearing takes from the other.
            OnPropertyChanged(nameof(RecentRollHeight));
        };

        FailedReports.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFailedReports));

        uploadQueue.Changed += (_, _) => ApplyQueueState();

        // A report reaching the site is what moves its photos into the reported section, and it can
        // happen while the user is sitting on this page watching.
        uploadQueue.Completed += (_, _) => ReloadAfterCompletion();
    }

    /// <summary>
    /// The photos that still need reporting, other than the ones just taken.
    /// </summary>
    public ObservableCollection<PhotoItemViewModel> PendingPhotos { get; }

    /// <summary>
    /// The unreported photos from the last few minutes.
    /// </summary>
    /// <remarks>
    /// Pinned above the rest of the roll rather than left in it. The user has just pointed their
    /// phone at something and come here to report it, and a roll of their own photos all looks
    /// alike, so the one they came for should not have to be picked out by eye.
    /// </remarks>
    public ObservableCollection<PhotoItemViewModel> RecentPhotos { get; }

    /// <summary>
    /// The photos that have already been reported.
    /// </summary>
    /// <remarks>
    /// These are kept apart from the rest and folded away by default. Once a photo has been sent it
    /// is history, and leaving it in the roll buries the one or two photos the user actually still
    /// has to do something about.
    /// </remarks>
    public ObservableCollection<PhotoItemViewModel> ReportedPhotos { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReportedHeader))]
    [NotifyPropertyChangedFor(nameof(ReportedRollHeight))]
    [NotifyPropertyChangedFor(nameof(RecentRollHeight))]
    public partial IReadOnlyList<ReportedPhotoGroupViewModel> ReportedGroups { get; private set; } =
        Array.Empty<ReportedPhotoGroupViewModel>();

    /// <summary>
    /// Every tile in the roll, whichever section it is in.
    /// </summary>
    private IEnumerable<PhotoItemViewModel> AllPhotos =>
        RecentPhotos.Concat(PendingPhotos).Concat(ReportedPhotos);

    /// <summary>
    /// The reports the app stopped trying to send.
    /// </summary>
    public ObservableCollection<FailedReportViewModel> FailedReports { get; }

    public bool HasFailedReports => FailedReports.Count > 0;

    /// <summary>
    /// What the queue is doing, when it is doing anything.
    /// </summary>
    /// <remarks>
    /// This is the confirmation the report sheet used to give with an alert. It is better placed
    /// here: it says what is actually true right now rather than what was true a moment ago, and it
    /// goes away by itself when the report lands.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueueSummary))]
    public partial string? QueueSummary { get; set; }

    public bool HasQueueSummary => !string.IsNullOrEmpty(QueueSummary);

    public bool HasPhotos => PendingPhotos.Count > 0 || RecentPhotos.Count > 0 || ReportedPhotos.Count > 0;

    public bool HasRecentPhotos => RecentPhotos.Count > 0;

    /// <summary>
    /// Whether the older photos need a heading of their own.
    /// </summary>
    /// <remarks>
    /// Only worth saying when there is a just taken section above them to tell them apart from.
    /// With nothing recent the roll is just the roll.
    /// </remarks>
    public bool HasEarlierPhotos => RecentPhotos.Count > 0 && PendingPhotos.Count > 0;

    public string RecentHeader => RecentPhotos.Count == 1 ? "Just taken" : $"Just taken ({RecentPhotos.Count})";

    /// <summary>
    /// How tall the just taken section should be.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="ReportedRollHeight"/>: a CollectionView in an auto sized row
    /// has nothing to measure against and collapses.
    /// </remarks>
    public double RecentRollHeight => PinnedRollHeights.Recent;

    public bool HasReportedPhotos => ReportedPhotos.Count > 0;

    public string ReportedHeader
    {
        get
        {
            int count = ReportedGroups.Count(group => group.SubmissionId is not null);
            string reports = count == 1 ? "1 report" : $"{count} reports";
            string photos = ReportedPhotos.Count == 1 ? "1 photo" : $"{ReportedPhotos.Count} photos";
            return count > 0
                ? $"Already reported - {reports} - {photos}"
                : $"Already reported - {photos}";
        }
    }

    /// <summary>
    /// Whether the already reported photos are folded out.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReportedToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(RecentRollHeight))]
    [NotifyPropertyChangedFor(nameof(ReportedRollHeight))]
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
    /// has to be told. Each report starts a new thumbnail row and has its own header. The viewport
    /// is capped so the section cannot crowd out the photos still waiting to be reported.
    /// </remarks>
    public double ReportedRollHeight => PinnedRollHeights.Reported;

    private (double Recent, double Reported) PinnedRollHeights => PhotoRollLayout.Measure(
        RecentPhotos.Count, ReportedGroups.Select(group => group.Count), AreReportedPhotosExpanded);

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
    public partial string? PhotoAccessMessage { get; set; }

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
    /// Whether the preview has rendered a frame and can accept camera input.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewInteractive))]
    [NotifyPropertyChangedFor(nameof(CanZoom))]
    public partial bool IsCameraReady { get; set; }

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
    public bool IsPreviewInteractive => HasCamera && IsCameraReady && !IsRollVisible;

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

    /// <summary>
    /// What counts as just taken, and what counts as the same thing being photographed.
    /// </summary>
    /// <remarks>
    /// Built from the site's own photo limit rather than a number of its own, so a report is never
    /// pre-filled with more photos than it is allowed to carry.
    /// </remarks>
    private RecentPhotoRules RecentRules => RecentPhotoRules.ForReport(MaxPhotosPerReport);

    public IReadOnlyList<PhotoItemViewModel> SelectedPhotos =>
        AllPhotos.Where(photo => photo.IsSelected).ToList();

    /// <summary>
    /// Whether the report button should be offered.
    /// </summary>
    /// <remarks>
    /// A selection outlives closing the roll, so this has to be tied to the roll being up as well.
    /// Otherwise the report button sits over the live camera preview, which is nowhere near the
    /// photos it would be reporting.
    ///
    /// Submitted photos remain selectable for deletion, but cannot be reported again. Queued
    /// photos are also excluded because they are already on their way to the site.
    /// </remarks>
    public bool CanReport
    {
        get
        {
            IReadOnlyList<PhotoItemViewModel> selected = SelectedPhotos;
            return IsRollVisible &&
                ReportValidator.ValidatePhotos(selected, MaxPhotosPerReport).IsValid &&
                !selected.Any(photo => photo.IsQueued);
        }
    }

    /// <summary>
    /// Whether the delete button should be offered.
    /// </summary>
    /// <remarks>
    /// Unlike reporting there is no cap, so a user clearing out a pile of old shots is not made to
    /// do it four at a time.
    /// </remarks>
    public bool CanDelete =>
        IsRollVisible &&
        SelectedPhotos.Count > 0 &&
        !SelectedPhotos.Any(photo => photo.IsQueued);

    /// <summary>
    /// Offers the photos of whatever the user has just photographed as soon as the roll is opened.
    /// </summary>
    partial void OnIsRollVisibleChanged(bool value)
    {
        if (value)
        {
            ApplyAutoSelection();
        }
    }

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
            PhotoLibraryAccess access;
            try
            {
                access = await photoLibrary.CheckAccessAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not check photo-library access.");
                access = PhotoLibraryAccess.Denied;
            }
            PhotoAccessMessage = access switch
            {
                PhotoLibraryAccess.Limited =>
                    "Photo access is limited. New camera photos will be kept only inside this app. You can still import photos with the system picker.",
                PhotoLibraryAccess.Denied or PhotoLibraryAccess.NotDetermined =>
                    "Photo access is off. New camera photos will be kept only inside this app. You can still import photos with the system picker.",
                _ => null
            };

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
            // Captured streams are large memory buffers, and stamping EXIF/XMP is synchronous CPU
            // work after the reads complete. Keep that work away from the shutter animation.
            ReportPhoto? photo = await Task.Run(async () =>
            {
                byte[] jpeg = await captureService.PrepareCapturedPhotoAsync(media);
                return await photoCatalog.AddCapturedPhotoAsync(jpeg);
            });

            if (photo is null)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusMessage = "Couldn't save that photo.");
                return null;
            }

            return await MainThread.InvokeOnMainThreadAsync(() => AddCapturedPhotoToRollAsync(photo));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to store a captured photo.");
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusMessage = "Couldn't save that photo.");
            return null;
        }
    }

    /// <summary>
    /// Adds a saved capture to the bound roll on the UI thread.
    /// </summary>
    private async Task<PhotoItemViewModel?> AddCapturedPhotoToRollAsync(ReportPhoto photo)
    {
        // A reload can complete while the photo is being saved, in which case it already picked
        // the new asset up and inserting again would put two tiles on screen for one photo.
        if (AllPhotos.Any(existing =>
                string.Equals(existing.Id, photo.Id, StringComparison.Ordinal)))
        {
            return null;
        }

        PhotoItemViewModel item = new PhotoItemViewModel(photo);

        // Straight into the just taken section: this is the photo the user is here for.
        RecentPhotos.Insert(0, item);
        await LoadThumbnailAsync(item);
        LatestThumbnail = item.Thumbnail;

        // Saving a photo takes long enough for the user to have opened the roll while it was
        // still going, in which case the suggestion was made without this photo in it.
        ApplyAutoSelection();

        return item;
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
    private void ToggleRoll()
    {
        if (!HasCamera)
        {
            IsRollVisible = true;
            return;
        }

        IsRollVisible = !IsRollVisible;
    }

    [RelayCommand]
    private void ToggleReportedPhotos() => AreReportedPhotosExpanded = !AreReportedPhotosExpanded;

    /// <summary>
    /// Adds or removes a photo from the selection.
    /// </summary>
    /// <remarks>
    /// The server refuses reports with more than four photos. Selecting a fifth is still allowed,
    /// because the selection also drives deleting and being made to clear a roll four photos at a
    /// time would be miserable, but the report button goes away and the reason is spelled out.
    ///
    /// Taking a photo out of the selection is also remembered, so the automatic selection does not
    /// put it straight back the next time the roll is opened.
    /// </remarks>
    public bool ToggleSelection(PhotoItemViewModel photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        photo.IsSelected = !photo.IsSelected;

        // From here the selection is the user's, not a suggestion.
        selectionAdjustedByUser = true;

        if (photo.IsSelected)
        {
            deselectedPhotoIds.Remove(photo.Id);
        }
        else
        {
            deselectedPhotoIds.Add(photo.Id);
        }

        IReadOnlyList<PhotoItemViewModel> selected = SelectedPhotos;
        ValidationResult validation = ReportValidator.ValidatePhotos(selected, MaxPhotosPerReport);

        StatusMessage = selected.Count > 0 && !validation.IsValid
            ? validation.Error
            : selected.Any(item => item.IsQueued)
                ? "One of these photos is already on its way to the site."
                : null;

        OnPropertyChanged(nameof(CanReport));
        OnPropertyChanged(nameof(CanDelete));
        return true;
    }

    /// <summary>
    /// Ticks the photos of whatever the user has just photographed.
    /// </summary>
    /// <remarks>
    /// The point of the app is the distance between seeing something and reporting it, and having
    /// to find your own photo in a grid of your own photos is most of what is left of that
    /// distance.
    ///
    /// It only ever suggests. An existing selection is left alone, because the user choosing photos
    /// is a better answer than anything worked out from timestamps, and a photo the user has taken
    /// out of a suggestion is never offered again.
    /// </remarks>
    private void ApplyAutoSelection()
    {
        if (!IsRollVisible)
        {
            return;
        }

        // The user's own choice always wins, however it turned out.
        if (selectionAdjustedByUser)
        {
            return;
        }

        RecentPhotoRules rules = RecentRules;
        DateTimeOffset now = DateTimeOffset.Now;

        // A report can only carry so many photos, so a burst of five leaves one behind when the
        // other four are sent. Photos of something already reported are dropped before the cluster
        // is worked out rather than after, so a leftover can neither start a suggestion of its own
        // nor be swept into the suggestion for the next thing photographed from the same spot.
        List<ReportPhoto> alreadySent = AllPhotos
            .Where(item => item.Submitted || item.IsQueued)
            .Select(item => item.Photo)
            .Where(photo => RecentPhotoSelector.IsRecent(photo, now, rules.RecencyWindow))
            .ToList();

        // A reported photo is finished with, and a queued one is already spoken for by a report on
        // its way out, so neither is something to offer.
        List<PhotoItemViewModel> candidates = RecentPhotos
            .Concat(PendingPhotos)
            .Where(item => !item.Submitted && !item.IsQueued)
            .Where(item => !HasAlreadyBeenReported(item.Photo, alreadySent, rules))
            .ToList();

        IReadOnlyList<ReportPhoto> cluster = RecentPhotoSelector.SelectCluster(
            candidates.Select(item => item.Photo).ToList(),
            now,
            rules);

        // The newest photo having been turned down means the user has already seen this suggestion
        // and said no to it, and the rest of the cluster is only there because of that photo.
        if (cluster.Count == 0 || deselectedPhotoIds.Contains(cluster[0].Id))
        {
            return;
        }

        // Whatever the last suggestion was, it was about an older photo than this one.
        foreach (PhotoItemViewModel item in AllPhotos.Where(item => autoSelectedPhotoIds.Contains(item.Id)))
        {
            item.IsSelected = false;
        }

        autoSelectedPhotoIds.Clear();

        HashSet<string> ids = cluster
            .Select(photo => photo.Id)
            .Where(id => !deselectedPhotoIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (PhotoItemViewModel item in candidates.Where(item => ids.Contains(item.Id)))
        {
            item.IsSelected = true;
            autoSelectedPhotoIds.Add(item.Id);
        }

        OnPropertyChanged(nameof(CanReport));
        OnPropertyChanged(nameof(CanDelete));
    }

    /// <summary>
    /// Whether a photo is of something the user has already sent a report about.
    /// </summary>
    /// <remarks>
    /// Asking whether it belongs with something already on its way is the same question the
    /// selection itself is built on.
    ///
    /// Only photos taken before the report was made count. Something photographed afterwards is a
    /// new thing seen, even from the same spot a minute later, and a busy block is exactly where
    /// this feature earns its keep.
    /// </remarks>
    private static bool HasAlreadyBeenReported(ReportPhoto photo,
        IReadOnlyList<ReportPhoto> alreadySent,
        RecentPhotoRules rules)
    {
        return alreadySent.Any(sent => WasTakenNoLaterThan(photo, sent) &&
            RecentPhotoSelector.BelongsWith(photo, sent, rules));
    }

    private static bool WasTakenNoLaterThan(ReportPhoto photo, ReportPhoto other) =>
        photo.CreatedAt is DateTimeOffset takenAt &&
        other.CreatedAt is DateTimeOffset otherTakenAt &&
        takenAt <= otherTakenAt;

    /// <summary>
    /// Describes what deleting the selection would do, when the user needs telling.
    /// </summary>
    /// <remarks>
    /// iOS already asks before it destroys photos the app took, so a second confirmation there is
    /// just an extra tap. Android lets the app delete its own MediaStore entries directly, so the
    /// shared confirmation has to cover captured photos there as well.
    /// </remarks>
    /// <returns>The message to confirm, or null when the platform's own prompt is enough.</returns>
    public string? BuildDeleteConfirmation()
    {
        IReadOnlyList<PhotoItemViewModel> selected = SelectedPhotos;
        int imported = selected.Count(photo => photo.IsImported);
        int privateCaptured = selected.Count(
            photo => photo.Photo.Origin == PhotoOrigin.PrivateCaptured);
        int captured = selected.Count - imported - privateCaptured;
        return PhotoDeletionConfirmation.Build(
            captured,
            imported,
            photoLibrary.ConfirmsCapturedPhotoDeletion,
            privateCaptured);
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
                RecentPhotos.Remove(item);
                PendingPhotos.Remove(item);
                ReportedPhotos.Remove(item);
            }

            RebuildReportedGroups();

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
    /// submitted photos ticked with the report button live, one tap away from a duplicate. What the
    /// user has just photographed is offered again at the end, once the queue has been read and it
    /// is known which photos are already spoken for.
    /// </remarks>
    public async Task ReloadPhotosAsync()
    {
        IReadOnlyList<ReportPhoto> photos = await photoCatalog.GetPhotosAsync();

        // Existing items are reused so their loaded thumbnails survive the refresh. A duplicate id
        // should not be possible, but ToDictionary would throw for the rest of the session if one
        // ever appeared, leaving the roll stuck on stale tiles.
        Dictionary<string, PhotoItemViewModel> existing = AllPhotos
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        RecentPhotos.Clear();
        PendingPhotos.Clear();
        ReportedPhotos.Clear();

        // The selection goes with the roll it was made against, so what is remembered about it has
        // to go too, or a suggestion could never be made again.
        autoSelectedPhotoIds.Clear();
        selectionAdjustedByUser = false;

        // Notified straight away so the report button hides for the duration of the reload rather
        // than sitting there enabled against a selection that no longer exists.
        OnPropertyChanged(nameof(CanReport));
        OnPropertyChanged(nameof(CanDelete));

        // One reading of the clock for the whole roll, so two photos a second apart cannot end up
        // in different sections.
        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan recencyWindow = RecentRules.RecencyWindow;

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
            else if (RecentPhotoSelector.IsRecent(photo, now, recencyWindow))
            {
                RecentPhotos.Add(item);
            }
            else
            {
                PendingPhotos.Add(item);
            }
        }

        RebuildReportedGroups();

        // Snapshot before awaiting: a capture finishing mid reload would otherwise mutate one of the
        // bound collections while this is still walking it.
        List<PhotoItemViewModel> loaded = AllPhotos.ToList();
        foreach (PhotoItemViewModel item in loaded)
        {
            await LoadThumbnailAsync(item);
        }

        RefreshLatestThumbnail();

        ApplyQueueState();

        ApplyAutoSelection();

        OnPropertyChanged(nameof(CanReport));
        OnPropertyChanged(nameof(CanDelete));
    }

    private void RebuildReportedGroups()
    {
        ReportedGroups = ReportedPhotoGrouper.Group(ReportedPhotos)
            .Select(group => new ReportedPhotoGroupViewModel(group))
            .ToList();

        foreach (PhotoItemViewModel photo in ReportedPhotos.Where(photo =>
                     string.IsNullOrWhiteSpace(photo.SubmissionId) || photo.SubmittedAt is null))
        {
            logger.LogWarning("Reported photo {Id} has incomplete submission details.", photo.Id);
        }

        if (ReportedPhotos.Count == 0)
        {
            AreReportedPhotosExpanded = false;
        }
    }

    /// <summary>
    /// Brings the roll into line with what the upload queue is doing.
    /// </summary>
    /// <remarks>
    /// Cheap enough to run on every queue change, and running it on every change is what keeps a
    /// photo's badge honest while a report is in flight.
    /// </remarks>
    private void ApplyQueueState()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(ApplyQueueState);
            return;
        }

        IReadOnlyList<QueuedReport> queued = uploadQueue.Reports;

        Dictionary<string, UploadQueueState> byPhoto = new Dictionary<string, UploadQueueState>(StringComparer.Ordinal);
        foreach (QueuedReport report in queued)
        {
            foreach (ReportPhoto photo in report.Photos)
            {
                byPhoto[photo.Id] = report.State;
            }
        }

        foreach (PhotoItemViewModel item in AllPhotos)
        {
            item.QueueState = byPhoto.TryGetValue(item.Id, out UploadQueueState state) ? state : null;
        }

        FailedReports.Clear();
        foreach (QueuedReport report in queued.Where(report => report.State == UploadQueueState.Failed))
        {
            FailedReports.Add(new FailedReportViewModel(report, uploadQueue));
        }

        int sending = queued.Count(report => report.State == UploadQueueState.Uploading);
        int waiting = queued.Count(report => report.State == UploadQueueState.Pending);

        QueueSummary = (sending, waiting) switch
        {
            (0, 0) => null,
            (> 0, 0) => "Sending your report…",
            (> 0, _) => $"Sending your report… {waiting} more waiting.",
            (0, 1) => "1 report waiting to send.",
            _ => $"{waiting} reports waiting to send."
        };

        OnPropertyChanged(nameof(CanReport));
    }

    private async void ReloadAfterCompletion()
    {
        try
        {
            await ReloadPhotosAsync();
        }
        catch (Exception ex)
        {
            // async void, reached from an event the queue raises on its own schedule.
            logger.LogError(ex, "Failed to refresh the roll after a report was sent.");
        }
    }

    /// <summary>
    /// Points the peek button at the newest photo the app knows about.
    /// </summary>
    /// <remarks>
    /// The roll is split into sections and each one is newest first, so the newest photo overall
    /// can be at the top of any of them.
    /// </remarks>
    private void RefreshLatestThumbnail()
    {
        PhotoItemViewModel? newest = null;

        foreach (PhotoItemViewModel item in AllPhotos)
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

        byte[]? jpeg = await photoCatalog.GetThumbnailAsync(item.Id, ThumbnailPixelSize);
        if (jpeg is not null)
        {
            item.Thumbnail = ImageSource.FromStream(() => new MemoryStream(jpeg));
        }
    }
}
