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
    private readonly IPhotoCatalog photoCatalog;
    private readonly IPhotoLibraryService photoLibrary;
    private readonly IAuthService authService;
    private readonly ILogger<ReportViewModel> logger;

    private IReadOnlyList<ReportPhoto> photos = Array.Empty<ReportPhoto>();

    public ReportViewModel(IUploadService uploadService,
        IPhotoCatalog photoCatalog,
        IPhotoLibraryService photoLibrary,
        IAuthService authService,
        ILogger<ReportViewModel> logger)
    {
        this.uploadService = uploadService;
        this.photoCatalog = photoCatalog;
        this.photoLibrary = photoLibrary;
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
            byte[]? thumbnail = await photoLibrary.GetThumbnailAsync(photo.Id, 480);
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
    /// Uploads the photos and submits the report.
    /// </summary>
    /// <returns>True when the report was accepted.</returns>
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
            List<UploadPhoto> toUpload = new List<UploadPhoto>(photos.Count);
            foreach (ReportPhoto photo in photos)
            {
                byte[]? jpeg = await photoLibrary.GetPhotoDataAsync(photo.Id, cancellationToken);
                if (jpeg is null)
                {
                    ErrorMessage = "One of the photos could not be read. It may have been deleted.";
                    return false;
                }

                toUpload.Add(new UploadPhoto(photo.Id, jpeg));
            }

            UploadPreparation preparation = await uploadService.PrepareAsync(toUpload, cancellationToken);

            // The server reads the photo's own EXIF, and it is more trustworthy than anything the
            // app guessed, so its answers win unless the user overrode them.
            if (!UserSpecifiedDateTime && preparation.PhotoDateTime is DateTime serverDate)
            {
                SetDateTime(serverDate, userSpecified: false);
            }

            if (!UserSpecifiedLocation && preparation.Location is GeoPosition serverLocation)
            {
                SetLocation(serverLocation, userSpecified: false);
            }

            CrossStreet ??= preparation.CrossStreet;

            draft = BuildDraft();
            draft.CrossStreet = CrossStreet;

            validation = ReportValidator.Validate(draft,
                photos.Count,
                uploadService.BoundingBox,
                uploadService.Limits.MaxPhotosPerReport,
                DateTime.Now);

            if (!validation.IsValid)
            {
                ErrorMessage = validation.Error;
                return false;
            }

            await uploadService.FinalizeAsync(preparation, draft, authService.CurrentIdentity, cancellationToken);

            await photoCatalog.MarkSubmittedAsync(photos, preparation.SubmissionId, cancellationToken);

            return true;
        }
        catch (UploadException ex)
        {
            ErrorMessage = ex.IsBlocked
                ? "This device can't submit reports."
                : ex.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit a report.");
            ErrorMessage = "Couldn't reach the site. Check your connection and try again.";
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
