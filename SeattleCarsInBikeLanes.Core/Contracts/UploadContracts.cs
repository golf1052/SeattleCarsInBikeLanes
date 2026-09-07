using System.Text.Json.Serialization;

namespace SeattleCarsInBikeLanes.Core.Contracts;

/// <summary>
/// What <c>POST /api/Upload/Initial</c> returns for each photo.
/// </summary>
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
/// The per-photo body of <c>POST /api/Upload/Finalize</c>.
/// </summary>
/// <remarks>
/// Contains only fields a client can legitimately set. Device, report, and authenticated Bluesky
/// identity fields are derived by the server and are deliberately absent.
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

    [JsonPropertyName("threadsSubmittedBy")]
    public string? ThreadsSubmittedBy { get; set; }

    [JsonPropertyName("twitterUsername")]
    public string? TwitterUsername { get; set; }

    [JsonPropertyName("twitterAccessToken")]
    public string? TwitterAccessToken { get; set; }

    [JsonPropertyName("mastodonEndpoint")]
    public string? MastodonEndpoint { get; set; }

    [JsonPropertyName("mastodonUsername")]
    public string? MastodonUsername { get; set; }

    [JsonPropertyName("mastodonFullUsername")]
    public string? MastodonFullUsername { get; set; }

    [JsonPropertyName("mastodonAccessToken")]
    public string? MastodonAccessToken { get; set; }

    [JsonPropertyName("threadsUsername")]
    public string? ThreadsUsername { get; set; }

    [JsonPropertyName("threadsAccessToken")]
    public string? ThreadsAccessToken { get; set; }
}

/// <summary>
/// The limits returned by <c>GET /api/Upload/Limits</c>.
/// </summary>
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinimumSupportedAppVersion { get; set; }
}
