using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.ViewModels;

/// <summary>
/// The report the user fills in before sending.
/// </summary>
public sealed partial class ReportViewModel : ObservableObject
{
    private readonly IUploadService uploadService;
    private readonly IUploadQueue uploadQueue;
    private readonly IPhotoCatalog photoCatalog;
    private readonly IAuthService authService;
    private readonly ILogger<ReportViewModel> logger;

    private IReadOnlyList<ReportPhoto> photos = Array.Empty<ReportPhoto>();

    public ReportViewModel(IUploadService uploadService,
        IUploadQueue uploadQueue,
    IPhotoCatalog photoCatalog,
        IAuthService authService,
        ILogger<ReportViewModel> logger)
    {
        this.uploadService = uploadService;
        this.uploadQueue = uploadQueue;
        this.photoCatalog = photoCatalog;
        this.authService = authService;
        this.logger = logger;

        Thumbnails = new ObservableCollection<ImageSource>();
    }

    public ObservableCollection<ImageSource> Thumbnails { get; }

    [ObservableProperty]
    public partial int NumberOfCars { get; set; } = 1;

    [ObservableProperty]
    public partial DateTime Date { get; set; } = DateTime.Now.Date;

    [ObservableProperty]
    public partial TimeSpan Time { get; set; } = DateTime.Now.TimeOfDay;

    [ObservableProperty]
    public partial string? CrossStreet { get; set; }

    [ObservableProperty]
    public partial bool HasLocation { get; set; }

    [ObservableProperty]
    public partial string? LocationDescription { get; set; }

    [ObservableProperty]
    public partial bool Attribute { get; set; }

    [ObservableProperty]
    public partial bool CanAttribute { get; set; }

    [ObservableProperty]
    public partial string? AttributionName { get; set; }

    [ObservableProperty]
    public partial bool IsUploading { get; set; }
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Set once the user has changed the date or time by hand.
    /// </summary>
    /// <remarks>
    /// The server keeps this so a moderator can tell a photo's own timestamp from one a person
    /// typed in.
    /// </remarks>
    public bool UserSpecifiedDateTime { get; private set; }

    public bool UserSpecifiedLocation { get; private set; }

    public GeoPosition? Location { get; private set; }

    /// <summary>
    /// Prepares the sheet for a set of photos.
    /// </summary>
    public async Task LoadAsync(IReadOnlyList<ReportPhoto> selectedPhotos)
    {
        ArgumentNullException.ThrowIfNull(selectedPhotos);

        photos = selectedPhotos;
        ErrorMessage = null;
        UserSpecifiedLocation = false;

        ReportPhoto? first = photos.FirstOrDefault();
        SetDateTime(first?.CreatedAt?.LocalDateTime ?? DateTime.Now, userSpecified: false);

        SetLocation(first?.Location, userSpecified: false);

        AttributionIdentity? identity = authService.CurrentIdentity;
        CanAttribute = identity?.CanAttribute == true;
        AttributionName = identity?.DisplayName;

        // Somebody who has asked to be credited should not have to say so on every report.
        Attribute = CanAttribute && AppPreferences.AttributeByDefault;

        Thumbnails.Clear();
        foreach (ReportPhoto photo in photos)
        {
            byte[]? thumbnail = await photoCatalog.GetThumbnailAsync(photo.Id, 480);
            if (thumbnail is not null)
            {
                Thumbnails.Add(ImageSource.FromStream(() => new MemoryStream(thumbnail)));
            }
        }
    }

    /// <summary>
    /// Applies a date and time, recording whether it came from the user or from the photo.
    /// </summary>
    /// <remarks>
    /// Seeding the pickers fires their change callbacks, which is indistinguishable from the user
    /// turning the dials. Routing every assignment through here, with the flag set last, is what
    /// keeps "the user typed this" honest. Getting that wrong means the server's own reading of the
    /// photo is thrown away and every report is flagged as hand entered for the moderator.
    /// </remarks>
    public void SetDateTime(DateTime value, bool userSpecified)
    {
        Date = value.Date;
        Time = value.TimeOfDay;
        UserSpecifiedDateTime = userSpecified;
    }

    /// <summary>
    /// Applies a position, whether it came from the photo or from the user.
    /// </summary>
    public void SetLocation(GeoPosition? location, bool userSpecified)
    {
        Location = location;
        HasLocation = location.HasValue;

        if (userSpecified)
        {
            UserSpecifiedLocation = true;

            // The old cross street belongs to the old position, and the server will work out a new
            // one from the coordinates.
            CrossStreet = null;
        }

        LocationDescription = location?.ToString() ?? "No location yet";
    }

    partial void OnDateChanged(DateTime value) => UserSpecifiedDateTime = true;

    partial void OnTimeChanged(TimeSpan value) => UserSpecifiedDateTime = true;

    /// <summary>
    /// Validates the report and hands it to the upload queue.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the network. The user is outdoors on a phone, and making them stand and
    /// watch a several megabyte upload is the wrong shape for the moment they are in, so the report
    /// is written down and sent afterwards.
    ///
    /// Everything the server could tell the app about the photos used to come back mid submit, and
    /// the report was re-checked against it. That is gone, and can be, because the check that runs
    /// here already insists on a date and an in-bounds location before a report is accepted at all.
    /// The server's reading of the EXIF is a refinement rather than a missing piece, so it is merged
    /// in when the report is actually sent and the user never has to be there for it.
    /// </remarks>
    /// <returns>True when the report was queued.</returns>
    public async Task<bool> SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (IsUploading)
        {
            return false;
        }

        ReportDraft draft = BuildDraft();

        ValidationResult validation = ReportValidator.Validate(draft,
            photos.Count,
            uploadService.BoundingBox,
            uploadService.Limits.MaxPhotosPerReport,
            DateTime.Now);

        if (!validation.IsValid)
        {
            ErrorMessage = validation.Error;
            return false;
        }

        IsUploading = true;
        ErrorMessage = null;

        try
        {
            if (!await uploadQueue.EnqueueAsync(photos, draft, cancellationToken))
            {
                // The only way to get here is a photo already spoken for by a report still on its
                // way out, which the roll would have shown had it caught up.
                ErrorMessage = "One of these photos has already been reported.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue a report.");
            ErrorMessage = "Couldn't save that report. Try again.";
            return false;
        }
        finally
        {
            IsUploading = false;
        }
    }

    [RelayCommand]
    private void ClearError() => ErrorMessage = null;

    private ReportDraft BuildDraft() => new ReportDraft()
    {
        NumberOfCars = NumberOfCars,
        TakenAt = Date.Date.Add(Time),
        Location = Location,
        UserSpecifiedDateTime = UserSpecifiedDateTime,
        UserSpecifiedLocation = UserSpecifiedLocation,
        Attribute = Attribute && CanAttribute,
        CrossStreet = CrossStreet
    };
}
