using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Upload;

/// <summary>
/// The report a user is filling in, before it becomes an upload.
/// </summary>
public sealed class ReportDraft
{
    /// <summary>
    /// Number of cars in the report. The server requires at least one.
    /// </summary>
    public int NumberOfCars { get; set; } = 1;

    /// <summary>
    /// When the report happened, local time.
    /// </summary>
    public DateTime? TakenAt { get; set; }

    /// <summary>
    /// Where the report happened.
    /// </summary>
    public GeoPosition? Location { get; set; }

    /// <summary>
    /// Set when the user typed the date and time rather than it coming from the photo.
    /// </summary>
    public bool UserSpecifiedDateTime { get; set; }

    /// <summary>
    /// Set when the user picked the location on the map rather than it coming from the photo.
    /// </summary>
    public bool UserSpecifiedLocation { get; set; }

    /// <summary>
    /// Whether the user wants the report credited to them.
    /// </summary>
    public bool Attribute { get; set; }

    /// <summary>
    /// The cross street, when the server has told us one.
    /// </summary>
    public string? CrossStreet { get; set; }

    /// <summary>
    /// Takes a copy, so a report can be sent without the queue's own record being changed.
    /// </summary>
    /// <remarks>
    /// Sending merges the server's reading of the photo into the draft, and a second attempt has to
    /// start from what the user actually filled in rather than from whatever the failed attempt
    /// left behind.
    /// </remarks>
    public ReportDraft Clone() => new ReportDraft()
    {
        NumberOfCars = NumberOfCars,
        TakenAt = TakenAt,
        Location = Location,
        UserSpecifiedDateTime = UserSpecifiedDateTime,
        UserSpecifiedLocation = UserSpecifiedLocation,
        Attribute = Attribute,
        CrossStreet = CrossStreet
    };
}

/// <summary>
/// Folds what the server read out of the photos into the report the user filled in.
/// </summary>
/// <remarks>
/// The server reads the EXIF itself, and for a photo that carries one its answer is better than
/// anything the app worked out. It is only ever a refinement though: the app will not queue a report
/// without a date and an in-bounds location, so nothing here can leave the draft less complete than
/// it arrived, and each value is only taken if it is usable on its own terms. Anything the user
/// typed is left alone, because they were looking at the thing being reported and the camera's clock
/// was not.
/// </remarks>
public static class ReportDraftMerge
{
    public static ReportDraft WithServerValues(ReportDraft draft,
        DateTime? photoDateTime,
        GeoPosition? location,
        string? crossStreet,
        BoundingBox boundingBox,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(boundingBox);

        ReportDraft merged = draft.Clone();

        // A camera with a wrong clock can report a date in the future, which the server would then
        // refuse at finalize, so the app's own answer stands in that case.
        if (!merged.UserSpecifiedDateTime && photoDateTime is DateTime serverDate && serverDate <= now)
        {
            merged.TakenAt = serverDate;
        }

        if (!merged.UserSpecifiedLocation && location is GeoPosition serverLocation &&
            boundingBox.Contains(serverLocation))
        {
            merged.Location = serverLocation;
        }

        // Only ever filled in, never replaced: a cross street the user moved the pin away from
        // belongs to the old position.
        if (string.IsNullOrWhiteSpace(merged.CrossStreet) && !merged.UserSpecifiedLocation)
        {
            merged.CrossStreet = crossStreet;
        }

        return merged;
    }
}

/// <summary>
/// Checks a report before it costs the user an upload.
/// </summary>
/// <remarks>
/// Checks both the server's report requirements and the app's local submission state before
/// uploading several megabytes over a cellular connection.
/// </remarks>
public static class ReportValidator
{
    public static ValidationResult ValidatePhotos(IReadOnlyList<IReportedPhoto> photos, int maxPhotos)
    {
        ArgumentNullException.ThrowIfNull(photos);

        if (photos.Any(photo => photo.Submitted))
        {
            return ValidationResult.Invalid(
                "Already reported photos can be deleted, but not reported again. Deselect them to report other photos.");
        }

        return photos.Count > maxPhotos
            ? ValidationResult.Invalid(
                $"A report can have at most {maxPhotos} photos. Deselect some to report, or delete them.")
            : ValidatePhotoCount(photos.Count, maxPhotos);
    }

    public static ValidationResult Validate(ReportDraft draft,
        int photoCount,
        BoundingBox boundingBox,
        int maxPhotos,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(boundingBox);

        ValidationResult photoValidation = ValidatePhotoCount(photoCount, maxPhotos);
        if (!photoValidation.IsValid)
        {
            return photoValidation;
        }

        if (draft.NumberOfCars < 1)
        {
            return ValidationResult.Invalid("Number of cars must be at least 1.");
        }

        if (draft.TakenAt is null)
        {
            return ValidationResult.Invalid("Pick the date and time this happened.");
        }

        if (draft.TakenAt.Value > now)
        {
            return ValidationResult.Invalid("The date and time must be in the past.");
        }

        if (draft.Location is null)
        {
            return ValidationResult.Invalid("Pick the location this happened.");
        }

        if (!boundingBox.Contains(draft.Location.Value))
        {
            return ValidationResult.Invalid("The photo wasn't taken in Seattle, so it can't be reported here.");
        }

        return ValidationResult.Valid;
    }

    private static ValidationResult ValidatePhotoCount(int photoCount, int maxPhotos)
    {
        if (photoCount < 1)
        {
            return ValidationResult.Invalid("Pick at least one photo.");
        }

        return photoCount > maxPhotos
            ? ValidationResult.Invalid($"A report can have at most {maxPhotos} photos.")
            : ValidationResult.Valid;
    }
}

/// <summary>
/// The outcome of validating a report.
/// </summary>
public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Valid { get; } = new ValidationResult(true, null);

    public static ValidationResult Invalid(string error) => new ValidationResult(false, error);
}
