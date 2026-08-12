using SeattleCarsInBikeLanes.Mobile.Core.Models;

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
}

/// <summary>
/// Checks a report before it costs the user an upload.
/// </summary>
/// <remarks>
/// Every rule here is also enforced by the server. The point is to fail in the moment, on the
/// phone, rather than after uploading several megabytes over a cellular connection.
/// </remarks>
public static class ReportValidator
{
    public static ValidationResult Validate(ReportDraft draft,
        int photoCount,
        BoundingBox boundingBox,
        int maxPhotos,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(boundingBox);

        if (photoCount < 1)
        {
            return ValidationResult.Invalid("Pick at least one photo.");
        }

        if (photoCount > maxPhotos)
        {
            return ValidationResult.Invalid($"A report can have at most {maxPhotos} photos.");
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
}

/// <summary>
/// The outcome of validating a report.
/// </summary>
public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Valid { get; } = new ValidationResult(true, null);

    public static ValidationResult Invalid(string error) => new ValidationResult(false, error);
}
