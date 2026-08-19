using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// A photo on its way to the site.
/// </summary>
public sealed record UploadPhoto(string Id, byte[] Jpeg);

/// <summary>
/// What came back from the first leg of an upload.
/// </summary>
public sealed record UploadPreparation(IReadOnlyList<InitialPhotoUpload> Photos)
{
    public string? SubmissionId => Photos.FirstOrDefault()?.SubmissionId;

    /// <summary>
    /// The date the server read out of the photos, if it found one.
    /// </summary>
    public DateTime? PhotoDateTime => Photos.FirstOrDefault(photo => photo.PhotoDateTime.HasValue)?.PhotoDateTime;

    public string? CrossStreet =>
        Photos.FirstOrDefault(photo => !string.IsNullOrWhiteSpace(photo.PhotoCrossStreet))?.PhotoCrossStreet;

    public GeoPosition? Location
    {
        get
        {
            InitialPhotoUpload? withLocation = Photos.FirstOrDefault(photo =>
                !string.IsNullOrWhiteSpace(photo.PhotoLatitude) && !string.IsNullOrWhiteSpace(photo.PhotoLongitude));

            if (withLocation is null ||
                !double.TryParse(withLocation.PhotoLatitude,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double latitude) ||
                !double.TryParse(withLocation.PhotoLongitude,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double longitude))
            {
                return null;
            }

            return new GeoPosition(latitude, longitude);
        }
    }
}

/// <summary>
/// Raised when the server refuses an upload, carrying the message it gave.
/// </summary>
/// <remarks>
/// The server explains itself in plain language ("Photo not taken in Seattle", "Photo does not pass
/// content check"), and that is far more use to the user than a status code, so it is preserved
/// rather than replaced with something generic.
/// </remarks>
public sealed class UploadException : Exception
{
    public UploadException(string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        bool isReportInFlight = false) : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        IsReportInFlight = isReportInFlight;
    }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsReportInFlight { get; }

    /// <summary>
    /// True when the device has been blocked from uploading.
    /// </summary>
    public bool IsBlocked => StatusCode == HttpStatusCode.Forbidden;
}

/// <summary>
/// Sends reports to the site.
/// </summary>
public interface IUploadService
{
    /// <summary>
    /// The limits the server enforces, refreshed from the site when possible.
    /// </summary>
    UploadLimits Limits { get; }

    BoundingBox BoundingBox { get; }

    Task RefreshLimitsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads the photos and returns what the server worked out about them.
    /// </summary>
    Task<UploadPreparation> PrepareAsync(IReadOnlyList<UploadPhoto> photos,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits the report the prepared photos belong to.
    /// </summary>
    Task FinalizeAsync(UploadPreparation preparation,
        ReportDraft draft,
        AttributionIdentity? identity,
        string reportId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class UploadService : IUploadService
{
    private const string ReportIdHeader = "X-Report-Id";
    private const string ReportInFlightHeader = "X-Report-In-Flight";

    /// <summary>
    /// The longest edge a photo is sent at.
    /// </summary>
    /// <remarks>
    /// The server resizes to the same bound anyway, so sending an untouched 12MP photo only costs
    /// the user time and cellular data. It reads the EXIF before resizing, so the downscale has to
    /// keep the metadata intact.
    /// </remarks>
    private const int MaxUploadEdge = 1920;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly IAuthService authService;
    private readonly IDeviceIdentityService deviceIdentity;
    private readonly IImageResizer imageResizer;
    private readonly ILogger<UploadService> logger;

    public UploadService(HttpClient httpClient,
        IAuthService authService,
        IDeviceIdentityService deviceIdentity,
        IImageResizer imageResizer,
        ILogger<UploadService> logger)
    {
        this.httpClient = httpClient;
        this.authService = authService;
        this.deviceIdentity = deviceIdentity;
        this.imageResizer = imageResizer;
        this.logger = logger;
    }

    public UploadLimits Limits { get; private set; } = new UploadLimits();

    public BoundingBox BoundingBox { get; private set; } = BoundingBox.Seattle;

    public async Task RefreshLimitsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            UploadLimits? limits = await httpClient.GetFromJsonAsync<UploadLimits>(SiteUrls.UploadLimits,
                JsonOptions,
                cancellationToken);

            if (limits is null)
            {
                return;
            }

            Limits = limits;
            BoundingBox = new BoundingBox(limits.SouthLatitude,
                limits.WestLongitude,
                limits.NorthLatitude,
                limits.EastLongitude);
        }
        catch (Exception ex)
        {
            // The built in defaults match the server, so an older build or a flaky network just
            // means the app keeps using what it already knows.
            logger.LogDebug(ex, "Could not refresh the upload limits.");
        }
    }

    public async Task<UploadPreparation> PrepareAsync(IReadOnlyList<UploadPhoto> photos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);

        if (photos.Count == 0)
        {
            throw new UploadException("Pick at least one photo.");
        }

        using MultipartFormDataContent content = new MultipartFormDataContent();
        for (int i = 0; i < photos.Count; i++)
        {
            byte[] jpeg = await imageResizer.ResizeAsync(photos[i].Jpeg, MaxUploadEdge, cancellationToken);
            ByteArrayContent photoContent = new ByteArrayContent(jpeg);
            photoContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

            // The server binds these to a List<IFormFile> named files.
            content.Add(photoContent, "files", $"photo{i}.jpg");
        }

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, SiteUrls.UploadInitial)
        {
            Content = content
        };

        await PrepareRequestAsync(request, cancellationToken);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await ThrowIfFailedAsync(response, cancellationToken);

        List<InitialPhotoUpload>? uploaded =
            await response.Content.ReadFromJsonAsync<List<InitialPhotoUpload>>(JsonOptions, cancellationToken);

        if (uploaded is null || uploaded.Count == 0)
        {
            throw new UploadException("The site accepted the photos but sent nothing back. Try again.");
        }

        return new UploadPreparation(uploaded);
    }

    public async Task FinalizeAsync(UploadPreparation preparation,
        ReportDraft draft,
        AttributionIdentity? identity,
        string reportId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);

        List<FinalizedPhotoUpload> body = FinalizeRequestBuilder.Build(preparation.Photos, draft, identity);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, SiteUrls.UploadFinalize)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };

        await PrepareRequestAsync(request, cancellationToken);
        request.Headers.TryAddWithoutValidation(ReportIdHeader, reportId);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await ThrowIfFailedAsync(response, cancellationToken);
    }

    private async Task PrepareRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await authService.AuthenticateAsync(request, cancellationToken);
        request.Headers.TryAddWithoutValidation("X-Device-Id", await deviceIdentity.GetDeviceIdAsync());
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        // The server sends its reasons as plain text, and they are written for users.
        string message = string.IsNullOrWhiteSpace(body)
            ? $"The site rejected the upload ({(int)response.StatusCode})."
            : body.Trim();

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset retryAt)
        {
            retryAfter = retryAt - DateTimeOffset.UtcNow;
        }

        if (retryAfter <= TimeSpan.Zero)
        {
            retryAfter = null;
        }

        bool isReportInFlight =
            response.Headers.TryGetValues(ReportInFlightHeader, out IEnumerable<string>? values) &&
            values.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        throw new UploadException(message, response.StatusCode, retryAfter, isReportInFlight);
    }
}
