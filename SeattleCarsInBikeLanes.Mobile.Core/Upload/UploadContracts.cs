using System.Text.Json.Serialization;

namespace SeattleCarsInBikeLanes.Mobile.Core.Upload;

/// <summary>
/// What <c>POST /api/Upload/Initial</c> returns for each photo.
/// </summary>
/// <remarks>
/// The server extracts the date, coordinates and cross street itself, so whatever comes back here
/// wins over what the app guessed. Any of it may be missing, which is the server's way of saying
/// the photo had no usable EXIF and the user has to supply the answer.
/// </remarks>
public sealed class InitialPhotoUpload
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("photoId")]
    public string PhotoId { get; set; } = string.Empty;

    [JsonPropertyName("submissionId")]
    public string SubmissionId { get; set; } = string.Empty;

    [JsonPropertyName("photoNumber")]
    public int PhotoNumber { get; set; }

    [JsonPropertyName("photoDateTime")]
    public DateTime? PhotoDateTime { get; set; }

    [JsonPropertyName("photoLatitude")]
    public string? PhotoLatitude { get; set; }

    [JsonPropertyName("photoLongitude")]
    public string? PhotoLongitude { get; set; }

    [JsonPropertyName("photoCrossStreet")]
    public string? PhotoCrossStreet { get; set; }

    [JsonPropertyName("tags")]
    public List<ImageTag> Tags { get; set; } = new List<ImageTag>();
}

public sealed class ImageTag
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public float Confidence { get; set; }
}

/// <summary>
/// The per photo body of <c>POST /api/Upload/Finalize</c>.
/// </summary>
/// <remarks>
/// This mirrors the server's <c>FinalizedPhotoUploadMetadata</c>, but only the fields a mobile
/// client legitimately sets. Fields the server fills in, clears, or derives from the authenticated
/// principal are deliberately absent: Bluesky identity in particular is read from the session and
/// anything sent here for it is ignored.
/// </remarks>
public sealed class FinalizedPhotoUpload
{
    [JsonPropertyName("photoId")]
    public string PhotoId { get; set; } = string.Empty;

    [JsonPropertyName("submissionId")]
    public string SubmissionId { get; set; } = string.Empty;

    [JsonPropertyName("photoNumber")]
    public int PhotoNumber { get; set; }

    [JsonPropertyName("photoDateTime")]
    public DateTime? PhotoDateTime { get; set; }

    [JsonPropertyName("photoLatitude")]
    public string? PhotoLatitude { get; set; }

    [JsonPropertyName("photoLongitude")]
    public string? PhotoLongitude { get; set; }

    [JsonPropertyName("photoCrossStreet")]
    public string? PhotoCrossStreet { get; set; }

    [JsonPropertyName("tags")]
    public List<ImageTag> Tags { get; set; } = new List<ImageTag>();

    [JsonPropertyName("numberOfCars")]
    public int? NumberOfCars { get; set; }

    [JsonPropertyName("userSpecifiedDateTime")]
    public bool UserSpecifiedDateTime { get; set; }

    [JsonPropertyName("userSpecifiedLocation")]
    public bool UserSpecifiedLocation { get; set; }

    [JsonPropertyName("attribute")]
    public bool? Attribute { get; set; }

    [JsonPropertyName("twitterSubmittedBy")]
    public string? TwitterSubmittedBy { get; set; }

    [JsonPropertyName("mastodonSubmittedBy")]
    public string? MastodonSubmittedBy { get; set; }

    [JsonPropertyName("blueskySubmittedBy")]
    public string? BlueskySubmittedBy { get; set; }

    [JsonPropertyName("mastodonEndpoint")]
    public string? MastodonEndpoint { get; set; }

    [JsonPropertyName("mastodonUsername")]
    public string? MastodonUsername { get; set; }

    [JsonPropertyName("mastodonFullUsername")]
    public string? MastodonFullUsername { get; set; }

    [JsonPropertyName("mastodonAccessToken")]
    public string? MastodonAccessToken { get; set; }
}

/// <summary>
/// The limits the server enforces on uploads.
/// </summary>
/// <remarks>
/// Fetched from <c>/api/Upload/Limits</c> so the app does not have to be rebuilt when they change.
/// The defaults match the server as of writing, and are used when the call fails.
/// </remarks>
public sealed class UploadLimits
{
    [JsonPropertyName("maxPhotosPerReport")]
    public int MaxPhotosPerReport { get; set; } = 4;

    [JsonPropertyName("southLatitude")]
    public double SouthLatitude { get; set; } = 47.495082;

    [JsonPropertyName("westLongitude")]
    public double WestLongitude { get; set; } = -122.436522;

    [JsonPropertyName("northLatitude")]
    public double NorthLatitude { get; set; } = 47.735525;

    [JsonPropertyName("eastLongitude")]
    public double EastLongitude { get; set; } = -122.235787;

    [JsonPropertyName("minimumSupportedAppVersion")]
    public string? MinimumSupportedAppVersion { get; set; }
}
