using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Xml;
using Azure;
using Azure.AI.ContentSafety;
using Azure.AI.Vision.ImageAnalysis;
using Azure.Maps.Search;
using Azure.Maps.Search.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ImageMagick;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UploadController : ControllerBase
    {
        public const string InitialUploadPrefix = "initialupload/";
        public const string FinalizedUploadPrefix = "finalizedupload/";
        public const string DeviceIdHeader = "X-Device-Id";
        public const string ReportIdHeader = "X-Report-Id";
        public const int MaxPhotosPerReport = 4;
        // Preserve the existing request ceiling; original photos can exceed the prepared-JPEG limit.
        private const long MaxInitialRequestBytes = 30_000_000;

        private readonly BoundingBox SeattleBoundingBox = new BoundingBox(
            new Position(-122.436522, 47.495082), new Position(-122.235787, 47.735525));
        private readonly ILogger<UploadController> logger;
        private readonly ImageAnalysisClient imageAnalysisClient;
        private readonly ContentSafetyClient contentSafetyClient;
        private readonly MapsSearchClient mapsSearchClient;
        private readonly BlobContainerClient blobContainerClient;
        private readonly MastodonCredentialVerifier mastodonVerifier;
        private readonly SlackbotProvider slackbotProvider;
        private readonly DeviceBlocklistProvider deviceBlocklistProvider;
        private readonly ReportStore reportStore;
        private readonly HelperMethods helperMethods;

        public UploadController(ILogger<UploadController> logger,
            ImageAnalysisClient imageAnalysisClient,
            ContentSafetyClient contentSafetyClient,
            MapsSearchClient mapsSearchClient,
            BlobContainerClient blobContainerClient,
            MastodonCredentialVerifier mastodonVerifier,
            SlackbotProvider slackbotProvider,
            DeviceBlocklistProvider deviceBlocklistProvider,
            ReportStore reportStore,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.imageAnalysisClient = imageAnalysisClient;
            this.contentSafetyClient = contentSafetyClient;
            this.mapsSearchClient = mapsSearchClient;
            this.blobContainerClient = blobContainerClient;
            this.mastodonVerifier = mastodonVerifier;
            this.slackbotProvider = slackbotProvider;
            this.deviceBlocklistProvider = deviceBlocklistProvider;
            this.reportStore = reportStore;
            this.helperMethods = helperMethods;
        }

        private string? DeviceId => DeviceIdRules.Normalize(Request.Headers[DeviceIdHeader].ToString());

        private string? ReportId => Request.Headers[ReportIdHeader].Count == 1 &&
            ReportStore.IsValidReportId(Request.Headers[ReportIdHeader].ToString())
                ? Request.Headers[ReportIdHeader].ToString() : null;

        private bool ValidDeviceContext => !Request.Headers.ContainsKey(DeviceIdHeader) ||
            Request.Headers[DeviceIdHeader].Count == 1 && DeviceIdRules.IsValid(DeviceId);

        [HttpGet("Limits")]
        public IActionResult GetLimits()
        {
            return Ok(new UploadLimits()
            {
                MaxPhotosPerReport = MaxPhotosPerReport,
                MaxPhotoBytes = ReportStore.MaxPhotoBytes,
                MaxReportBytes = ReportStore.MaxReportBytes,
                SouthLatitude = SeattleBoundingBox.Min.Latitude,
                WestLongitude = SeattleBoundingBox.Min.Longitude,
                NorthLatitude = SeattleBoundingBox.Max.Latitude,
                EastLongitude = SeattleBoundingBox.Max.Longitude
            });
        }

        [HttpPost("Initial")]
        [RequestSizeLimit(MaxInitialRequestBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxInitialRequestBytes)]
        public async Task<IActionResult> UploadPhoto([FromForm] List<IFormFile> files,
            CancellationToken cancellationToken = default)
        {
            if (ReportId is not { } id || !ValidDeviceContext)
            {
                return BadRequest("A valid report ID and, when supplied, device ID are required.");
            }
            if (files is not { Count: > 0 and <= MaxPhotosPerReport })
            {
                return BadRequest("Upload one to four photos.");
            }
            if (files.Any(f => f is null || f.Length is <= 0 or > MaxInitialRequestBytes) ||
                        files.Sum(f => f.Length) > MaxInitialRequestBytes)
            {
                return BadRequest("Upload nonempty photos within the 30 MB request limit.");
            }
            try
            {
                // Reusing an ID must never rebind an existing report to another installation.
                await reportStore.GetAsync(id, DeviceId, cancellationToken);
                if (await deviceBlocklistProvider.IsBlocked(DeviceId, cancellationToken))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, "This device can't submit reports.");
                }
                string submissionId = helperMethods.GetRandomFileName();
                async Task<(InitialPhotoUpload? Photo, string? Error)> Prepare(IFormFile file, int index)
                {
                    try
                    {
                        return (await ProcessInitialUpload(file, submissionId, id, DeviceId, index, cancellationToken), null);
                    }
                    catch (BikeLaneException ex)
                    {
                        return (null, $"Photo {index + 1}: {ex.Message}");
                    }
                    catch (MagickException)
                    {
                        return (null, $"Photo {index + 1}: The file could not be read as an image.");
                    }
                }

                // Catch validation in each task, but let cancellation and infrastructure failures escape.
                var prepared = await Task.WhenAll(files.Select(Prepare));
                string[] errors = prepared.Where(p => p.Error is not null).Select(p => p.Error!).ToArray();
                return errors.Length > 0
                    ? BadRequest(string.Join("\n", errors))
                    : Ok(prepared.Select(p => p.Photo!).ToArray());
            }
            catch (UnauthorizedAccessException)
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (ReportInProgressException ex)
            {
                return InProgress(ex);
            }
            catch (Exception ex) when (IsUnavailable(ex, cancellationToken))
            {
                return Unavailable("Photo preparation is temporarily unavailable. Please retry.");
            }
        }

        private async Task<InitialPhotoUpload> ProcessInitialUpload(IFormFile file, string submissionId,
            string reportId, string? deviceId, int index, CancellationToken cancellationToken)
        {
            using Stream input = file.OpenReadStream();
            using var original = await ReadBoundedAsync(input, MaxInitialRequestBytes, cancellationToken);
            if (original.Length != file.Length)
            {
                throw new BikeLaneException("The uploaded image length changed.");
            }
            using var image = new MagickImage(original);
            (DateTime? photoDate, Position? photoLocation) = GetPhotoMetadata(image);
            if (photoLocation is not null && (!double.IsFinite(photoLocation.Latitude) ||
                !double.IsFinite(photoLocation.Longitude)))
            {
                throw new BikeLaneException("The photo's location metadata is invalid.");
            }
            if (photoLocation is not null && !SeattleBoundingBox.Contains(photoLocation))
            {
                string latitude = photoLocation.Latitude.ToString("G", CultureInfo.InvariantCulture);
                string longitude = photoLocation.Longitude.ToString("G", CultureInfo.InvariantCulture);
                string label = $"{photoLocation.Latitude.ToString("0.#####", CultureInfo.InvariantCulture)}, " +
                    photoLocation.Longitude.ToString("0.#####", CultureInfo.InvariantCulture);
                throw new BikeLaneException("Photo not taken in Seattle. The location on the photo is " +
                    $"<a href=\"https://bing.com/maps?cp={latitude}~{longitude}&amp;lvl=13&amp;sp=point.{latitude}_{longitude}_Photo%20location___\" " +
                    $"target=\"_blank\" rel=\"noopener noreferrer\">{label}</a>");
            }

            image.Format = MagickFormat.Jpeg;
            if (image.Width >= image.Height && image.Width > 1920)
            {
                image.Resize(1920, 1080);
            }
            else if (image.Height > image.Width && image.Height > 1920)
            {
                image.Resize(1080, 1920);
            }
            using var jpeg = new MemoryStream();
            await image.WriteAsync(jpeg, cancellationToken);
            if (jpeg.Length is <= 0 or > ReportStore.MaxPhotoBytes)
            {
                throw new BikeLaneException("The prepared image must be no larger than 8 MiB.");
            }
            BinaryData bytes = new BinaryData(jpeg.GetBuffer().AsMemory(0, checked((int)jpeg.Length)));

            Task<Response<AnalyzeImageResult>> safetyTask =
                contentSafetyClient.AnalyzeImageAsync(bytes, cancellationToken: cancellationToken);
            Task<Response<ImageAnalysisResult>> analysisTask =
                imageAnalysisClient.AnalyzeAsync(bytes, VisualFeatures.Tags, cancellationToken: cancellationToken);
            Task<ReverseSearchCrossStreetAddressResultItem?> crossStreetTask = photoLocation is null
                ? Task.FromResult<ReverseSearchCrossStreetAddressResultItem?>(null)
                : helperMethods.ReverseSearchCrossStreet(photoLocation, mapsSearchClient).WaitAsync(cancellationToken);
            await Task.WhenAll(safetyTask, analysisTask, crossStreetTask);
            if (safetyTask.Result.Value.CategoriesAnalysis.Any(c => c.Severity > 2))
            {
                throw new BikeLaneException("Photo does not pass content check.");
            }
            if (photoLocation is not null && crossStreetTask.Result is null)
            {
                throw new BikeLaneException("Could not determine cross street.");
            }
            string photoId = helperMethods.GetRandomFileName();
            InitialPhotoUploadMetadata metadata = new InitialPhotoUploadMetadata()
            {
                PhotoId = photoId,
                SubmissionId = submissionId,
                ReportId = reportId,
                DeviceId = deviceId,
                PhotoNumber = index,
                PhotoDateTime = photoDate,
                PhotoLatitude = photoLocation?.Latitude.ToString("G", CultureInfo.InvariantCulture),
                PhotoLongitude = photoLocation?.Longitude.ToString("G", CultureInfo.InvariantCulture),
                PhotoCrossStreet = crossStreetTask.Result?.Address.StreetName,
                Tags = analysisTask.Result.Value.Tags.Values.Select(t => new ImageTag()
                {
                    Name = t.Name,
                    Confidence = t.Confidence
                }).ToList()
            };
            BlobClient photo = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{photoId}.jpeg");
            BlobClient storedMetadata = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{photoId}.json");
            await Task.WhenAll(
                photo.UploadAsync(bytes, new BlobUploadOptions()
                {
                    Conditions = new BlobRequestConditions() { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders() { ContentType = "image/jpeg" }
                }, cancellationToken),
                storedMetadata.UploadAsync(new BinaryData(metadata), new BlobUploadOptions()
                {
                    Conditions = new BlobRequestConditions() { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders() { ContentType = "application/json" }
                }, cancellationToken));
            Uri preview = await photo.GenerateUserDelegationReadOnlySasUri(DateTimeOffset.UtcNow.AddMinutes(10));
            return metadata.ToContract(preview.ToString());
        }

        private static async Task<MemoryStream> ReadBoundedAsync(Stream source, long maximum,
            CancellationToken cancellationToken)
        {
            MemoryStream result = new MemoryStream();
            try
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (result.Length + count > maximum)
                    {
                        throw new BikeLaneException("The uploaded data exceeds the size limit.");
                    }
                    result.Write(buffer, 0, count);
                }
                result.Position = 0;
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private (DateTime? Date, Position? Location) GetPhotoMetadata(MagickImage image)
        {
            DateTime? date = null;
            double? latitude = null;
            double? longitude = null;
            try
            {
                IExifProfile? exif = image.GetExifProfile();
                date = ReadExifDate(exif?.GetValue(ExifTag.DateTimeDigitized)?.Value) ??
                    ReadExifDate(exif?.GetValue(ExifTag.DateTimeOriginal)?.Value);
                latitude = ReadExifCoordinate(exif?.GetValue(ExifTag.GPSLatitude)?.Value,
                    exif?.GetValue(ExifTag.GPSLatitudeRef)?.Value, latitude: true);
                longitude = ReadExifCoordinate(exif?.GetValue(ExifTag.GPSLongitude)?.Value,
                    exif?.GetValue(ExifTag.GPSLongitudeRef)?.Value, latitude: false);
            }
            catch (Exception ex) when (ex is MagickException or ArgumentException or FormatException)
            {
                logger.LogWarning("Malformed EXIF metadata could not be read; trying XMP for missing fields.");
            }

            if (date is null || latitude is null || longitude is null)
            {
                Dictionary<string, string> xmp = ReadXmp(image);
                foreach (string field in new[] { "CreateDate", "DateTimeDigitized", "DateTimeOriginal" })
                {
                    if (date is not null)
                    {
                        break;
                    }
                    if (!xmp.TryGetValue(field, out string? text))
                    {
                        continue;
                    }
                    if (TryXmpDate(text, out DateTime value))
                    {
                        date = value;
                    }
                    else
                    {
                        logger.LogWarning("Malformed XMP capture date in {Field} was ignored.", field);
                    }
                }
                latitude ??= ReadXmpCoordinate(xmp, latitude: true);
                longitude ??= ReadXmpCoordinate(xmp, latitude: false);
            }
            return (date, latitude is { } lat && longitude is { } lon ? new Position(lon, lat) : null);
        }

        private DateTime? ReadExifDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            if (DateTime.TryParseExact(text.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime date))
            {
                return date;
            }
            logger.LogWarning("Malformed EXIF capture date was ignored.");
            return null;
        }

        private double? ReadExifCoordinate(Rational[]? values, string? reference, bool latitude)
        {
            if (values is null && string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }
            if (values?.Length == 3 && TryHemisphere(reference, latitude, out int sign) &&
                        TryDegrees(values.Select(v => v.ToDouble()).ToArray(), latitude, out double degrees))
            {
                return sign * degrees;
            }
            logger.LogWarning("Malformed EXIF GPS {Coordinate} was ignored.", latitude ? "latitude" : "longitude");
            return null;
        }

        private Dictionary<string, string> ReadXmp(MagickImage image)
        {
            const int maxXmpBytes = 256 * 1024;
            try
            {
                IXmpProfile? profile = image.GetXmpProfile();
                if (profile is null)
                {
                    return [];
                }
                if (profile.ToReadOnlySpan().Length > maxXmpBytes)
                {
                    logger.LogWarning("Oversized XMP metadata was ignored.");
                    return [];
                }
                using var stream = new MemoryStream(profile.ToByteArray());
                using var reader = XmlReader.Create(stream, new XmlReaderSettings()
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = maxXmpBytes,
                    MaxCharactersFromEntities = 1024,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true
                });
                Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
                while (!reader.EOF)
                {
                    if (reader.Depth > 32)
                    {
                        throw new XmlException("XMP nesting exceeds the limit.");
                    }
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (IsCaptureXmpField(reader.NamespaceURI, reader.LocalName))
                        {
                            string name = reader.LocalName;
                            fields.TryAdd(name, reader.ReadElementContentAsString().Trim());
                            continue;
                        }
                        while (reader.MoveToNextAttribute())
                        {
                            if (IsCaptureXmpField(reader.NamespaceURI, reader.LocalName))
                            {
                                fields.TryAdd(reader.LocalName, reader.Value.Trim());
                            }
                        }
                        reader.MoveToElement();
                    }
                    reader.Read();
                }
                return fields;
            }
            catch (Exception ex) when (ex is XmlException or MagickException or ArgumentException)
            {
                // XML exceptions can contain uploaded text or entity locations; never log their payload.
                logger.LogWarning("Malformed or unsafe XMP metadata was ignored.");
                return [];
            }
        }

        private static bool IsCaptureXmpField(string ns, string name)
        {
            return ns == "http://ns.adobe.com/xap/1.0/" && name == "CreateDate" ||
                                    ns == "http://ns.adobe.com/exif/1.0/" && name is
                                        "DateTimeDigitized" or "DateTimeOriginal" or "GPSLatitude" or "GPSLongitude" or
                                        "GPSLatitudeRef" or "GPSLongitudeRef";
        }

        private static bool TryXmpDate(string text, out DateTime date)
        {
            date = default;
            if (text.Length > 128)
            {
                return false;
            }
            string[] localFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
            "yyyy:MM:dd HH:mm:ss"];
            if (DateTime.TryParseExact(text, localFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return true;
            }
            string withOffset = text.EndsWith('Z') ? text[..^1] + "+00:00" : text;
            if (!DateTimeOffset.TryParseExact(withOffset,
                ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset capture))
            {
                return false;
            }        // The report form edits the camera's wall-clock date, not a date converted to server time.
            date = capture.DateTime;
            return true;
        }

        private double? ReadXmpCoordinate(Dictionary<string, string> fields, bool latitude)
        {
            string name = latitude ? "GPSLatitude" : "GPSLongitude";
            if (!fields.TryGetValue(name, out string? text))
            {
                return null;
            }
            fields.TryGetValue(name + "Ref", out string? reference);
            if (TryXmpCoordinate(text, reference, latitude, out double value))
            {
                return value;
            }
            logger.LogWarning("Malformed XMP GPS {Coordinate} was ignored.", latitude ? "latitude" : "longitude");
            return null;
        }

        private static bool TryXmpCoordinate(string text, string? reference, bool latitude, out double coordinate)
        {
            coordinate = 0;
            if (text.Length is 0 or > 128)
            {
                return false;
            }
            text = text.Trim();
            if (text.Length == 0)
            {
                return false;
            }
            char suffix = char.ToUpperInvariant(text[^1]);
            if (suffix is 'N' or 'S' or 'E' or 'W')
            {
                if (!string.IsNullOrWhiteSpace(reference) &&
                                                    !string.Equals(reference.Trim(), suffix.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                reference = suffix.ToString();
                text = text[..^1].Trim();
            }
            string[] parts = text.Replace("degrees", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("deg", " ", StringComparison.OrdinalIgnoreCase)
                .Split([',', ' ', '\t', '\r', '\n', ':', '°', '\'', '"', '′', '″'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 3)
            {
                return false;
            }
            double[] values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string[] fraction = parts[i].Split('/');
                if (fraction.Length is < 1 or > 2 ||
                    !double.TryParse(fraction[0], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    return false;
                }
                if (fraction.Length == 2)
                {
                    if (!double.TryParse(fraction[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                                                out double denominator) || !double.IsFinite(denominator) || denominator <= 0)
                    {
                        return false;
                    }
                    values[i] /= denominator;
                }
            }
            int sign = values[0] < 0 ? -1 : 1;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                if (!TryHemisphere(reference, latitude, out int hemisphere) || sign < 0 && hemisphere > 0)
                {
                    return false;
                }
                sign = hemisphere;
            }
            values[0] = Math.Abs(values[0]);
            if (!TryDegrees(values, latitude, out double degrees))
            {
                return false;
            }
            coordinate = sign * degrees;
            return true;
        }

        private static bool TryHemisphere(string? reference, bool latitude, out int sign)
        {
            string? hemisphere = reference?.Trim().ToUpperInvariant();
            sign = hemisphere is "S" or "W" ? -1 : 1;
            return latitude ? hemisphere is "N" or "S" : hemisphere is "E" or "W";
        }

        private static bool TryDegrees(double[] values, bool latitude, out double degrees)
        {
            degrees = 0;
            if (values.Any(v => !double.IsFinite(v) || v < 0) ||
                values.Skip(1).Any(v => v >= 60))
            {
                return false;
            }
            degrees = values[0] + (values.Length > 1 ? values[1] / 60 : 0) +
                        (values.Length > 2 ? values[2] / 3600 : 0);
            return degrees <= (latitude ? 90 : 180);
        }

        // Kept for existing callers; finalization uses the explicit attribution resolver instead.
        [NonAction]
        public async Task<bool?> ApplyBlueskyIdentity(List<FinalizedPhotoUploadMetadata> data)
        {
            AuthenticateResult auth = await HttpContext.AuthenticateAsync(BlueskyAuthDefaults.AnyScheme);
            string? did = auth.Succeeded ? auth.Principal.FindFirstValue(BlueskyAuthDefaults.DidClaim) : null;
            string? handle = auth.Succeeded ? auth.Principal.FindFirstValue(BlueskyAuthDefaults.HandleClaim) : null;
            bool signedIn = !string.IsNullOrWhiteSpace(did) && !string.IsNullOrWhiteSpace(handle);
            bool requested = data.Any(d => d.BlueskySubmittedBy?.StartsWith("Submitted by", StringComparison.Ordinal) == true);
            foreach (var photo in data)
            {
                photo.BlueskyUserDid = signedIn ? did : null;
                photo.BlueskyHandle = signedIn ? handle : null;
                if (!signedIn && requested)
                {
                    photo.BlueskySubmittedBy = "Submission";
                }
            }
            return requested && !signedIn ? false : signedIn ? true : null;
        }

        [HttpGet("Reports/{id}")]
        public async Task<IActionResult> ReportStatus(string id, CancellationToken cancellationToken = default)
        {
            if (!ReportStore.IsValidReportId(id) || !ValidDeviceContext)
            {
                return BadRequest("A valid report ID and, when supplied, device ID are required.");
            }
            try
            {
                SubmissionReport? report = await reportStore.GetAsync(id, DeviceId, cancellationToken);
                return report is null ? NotFound() : Ok(report.Receipt);
            }
            catch (UnauthorizedAccessException)
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (ReportInProgressException ex)
            {
                return InProgress(ex);
            }
            catch (Exception ex) when (IsUnavailable(ex, cancellationToken))
            {
                return Unavailable("Report recovery is temporarily unavailable. Please retry.");
            }
        }

        [HttpPost("Finalize")]
        [RequestSizeLimit(256 * 1024)]
        [ValidateFinalizationAfterReceipt]
        public async Task<IActionResult> FinalizeUpload([FromBody] FinalizeReportRequest request,
            CancellationToken cancellationToken = default)
        {
            if (ReportId is not { } id || !ValidDeviceContext)
            {
                return BadRequest("A valid report ID and, when supplied, device ID are required.");
            }
            try
            {
                // The first receipt remains authoritative after credential expiry, cleanup and moderation.
                SubmissionReport? existing = await reportStore.GetAsync(id, DeviceId, cancellationToken);
                if (existing is not null)
                {
                    return Ok(existing.Receipt);
                }
                if (await deviceBlocklistProvider.IsBlocked(DeviceId, cancellationToken))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, "This device can't submit reports.");
                }
                if (request?.Photos is not { Count: > 0 and <= MaxPhotosPerReport } ||
                    request.Attribution is null || request.Photos.Any(p => p is null))
                {
                    return BadRequest("A report needs one to four photos and explicit attribution.");
                }
                FinalizedPhotoUpload first = request.Photos[0];
                if (!ValidReportFields(first, out double latitude, out double longitude) ||
                    request.Photos.Any(p => !SameReportFields(first, p)))
                {
                    return BadRequest("All photos need the same date, Seattle location, positive car count and report fields.");
                }
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < request.Photos.Count; i++)
                {
                    FinalizedPhotoUpload photo = request.Photos[i];
                    if (!ValidPreparationId(photo.PhotoId) || !ValidPreparationId(photo.SubmissionId) ||
                        !ids.Add(photo.PhotoId) || photo.PhotoNumber != i || photo.SubmissionId != first.SubmissionId)
                    {
                        return BadRequest("Invalid ordered prepared photo set.");
                    }
                }

                ReportAttribution intent;
                try
                {
                    intent = NormalizeAttribution(request.Attribution);
                }
                catch (ArgumentException)
                {
                    return BadRequest("Invalid report attribution.");
                }
                string? blueskyHandle = null;
                if (intent.BlueskyDid is { } intendedDid)
                {
                    // An explicitly supplied bearer must never fall through to a different website account.
                    bool bearerSupplied = Request.Headers.Authorization.Any(value =>
                        value?.TrimStart() is { } header &&
                        header.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) &&
                        (header.Length == 6 || char.IsWhiteSpace(header[6])));
                    AuthenticateResult auth = await HttpContext.AuthenticateAsync(bearerSupplied
                        ? BlueskyAuthDefaults.BearerScheme : BlueskyAuthDefaults.CookieScheme);
                    if (!auth.Succeeded)
                    {
                        if (auth.Failure is not null && IsUnavailable(auth.Failure, cancellationToken))
                        {
                            return Unavailable("Bluesky could not verify the account. Please retry.");
                        }
                        return RejectedCredential("The Bluesky credential is no longer valid.");
                    }
                    string? actualDid = auth.Principal.FindFirstValue(BlueskyAuthDefaults.DidClaim);
                    blueskyHandle = auth.Principal.FindFirstValue(BlueskyAuthDefaults.HandleClaim);
                    if (string.IsNullOrWhiteSpace(actualDid) || string.IsNullOrWhiteSpace(blueskyHandle))
                    {
                        return RejectedCredential("The Bluesky credential has no verified identity.");
                    }
                    if (!string.Equals(actualDid, intendedDid, StringComparison.Ordinal))
                    {
                        return Conflict(new UploadError(UploadErrors.IdentityMismatch,
                            "The Bluesky credential does not match the selected account."));
                    }
                }

                VerifiedMastodonAccount? mastodon = null;
                if (intent.MastodonServer is { } server)
                {
                    if (string.IsNullOrWhiteSpace(first.MastodonAccessToken))
                    {
                        return RejectedCredential("The selected Mastodon account requires its credential.");
                    }
                    if (first.MastodonAccessToken.Length > 8192 || first.MastodonAccessToken.Any(char.IsControl) ||
                        request.Photos.Any(p => p.MastodonAccessToken != first.MastodonAccessToken))
                    {
                        return BadRequest("Invalid report credentials.");
                    }
                    mastodon = await mastodonVerifier.VerifyAsync(server, first.MastodonAccessToken, cancellationToken);
                    if (!string.Equals(mastodon.Id, intent.MastodonAccountId, StringComparison.Ordinal))
                    {
                        return Conflict(new UploadError(UploadErrors.IdentityMismatch,
                            "The Mastodon credential does not match the selected account."));
                    }
                }

                List<PreparedSubmissionPhoto> prepared = [];
                long totalBytes = 0;
                for (int i = 0; i < request.Photos.Count; i++)
                {
                    FinalizedPhotoUpload supplied = request.Photos[i];
                    InitialPhotoUploadMetadata stored;
                    BlobProperties properties;
                    string sourceName = $"{InitialUploadPrefix}{supplied.PhotoId}.jpeg";
                    try
                    {
                        stored = await ReadPreparationAsync(supplied.PhotoId, cancellationToken);
                        properties = (await blobContainerClient.GetBlobClient(sourceName)
                            .GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
                    }
                    catch (RequestFailedException ex) when (IsMissingOrChangedPreparation(ex))
                    {
                        return PreparationExpired();
                    }
                    if (stored.PhotoId != supplied.PhotoId || stored.PhotoNumber != i ||
                        stored.SubmissionId != first.SubmissionId || stored.ReportId != id ||
                        !string.Equals(stored.DeviceId, DeviceId, StringComparison.Ordinal))
                    {
                        return BadRequest("The prepared photos do not belong to this report and submitting context.");
                    }
                    if (properties.ContentLength is <= 0 or > ReportStore.MaxPhotoBytes ||
                        (totalBytes += properties.ContentLength) > ReportStore.MaxReportBytes)
                    {
                        return BadRequest("Prepared photos exceed the upload size limits.");
                    }
                    if (!string.Equals(properties.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(properties.ETag.ToString()))
                    {
                        return BadRequest("The prepared source is not a versioned JPEG image.");
                    }
                    if (stored.Tags is null || stored.Tags.Count > 1024 ||
                        stored.Tags.Any(t => t is null || t.Name is null || t.Name.Length > 1024 ||
                            !float.IsFinite(t.Confidence)))
                    {
                        throw new InvalidDataException("Invalid stored analysis.");
                    }
                    // Construct an allowlist rather than copying client identity, tokens or admin fields.
                    FinalizedPhotoUploadMetadata metadata = new FinalizedPhotoUploadMetadata()
                    {
                        PhotoId = $"{id}-{i}",
                        SubmissionId = id,
                        ReportId = id,
                        DeviceId = DeviceId,
                        PhotoNumber = i,
                        PhotoDateTime = first.PhotoDateTime,
                        PhotoLatitude = latitude.ToString("G", CultureInfo.InvariantCulture),
                        PhotoLongitude = longitude.ToString("G", CultureInfo.InvariantCulture),
                        PhotoCrossStreet = first.PhotoCrossStreet?.Trim(),
                        NumberOfCars = first.NumberOfCars,
                        UserSpecifiedDateTime = first.UserSpecifiedDateTime,
                        UserSpecifiedLocation = first.UserSpecifiedLocation,
                        Tags = stored.Tags,
                        Attribute = !intent.IsAnonymous,
                        TwitterSubmittedBy = "Submission",
                        ThreadsSubmittedBy = "Submission",
                        BlueskyUserDid = intent.BlueskyDid,
                        BlueskyHandle = blueskyHandle,
                        BlueskySubmittedBy = blueskyHandle is null ? "Submission" : $"Submitted by @{blueskyHandle}",
                        MastodonEndpoint = mastodon?.Server,
                        MastodonUsername = mastodon?.Username,
                        MastodonFullUsername = mastodon?.FullUsername,
                        MastodonSubmittedBy = mastodon is null ? "Submission" : $"Submitted by {mastodon.FullUsername}"
                    };
                    prepared.Add(new PreparedSubmissionPhoto(metadata, sourceName, properties.ETag.ToString(),
                        properties.ContentLength));
                }

                if (string.IsNullOrWhiteSpace(first.PhotoCrossStreet))
                {
                    string? crossStreet = (await helperMethods.ReverseSearchCrossStreet(
                        new Position(longitude, latitude), mapsSearchClient).WaitAsync(cancellationToken))?.Address.StreetName;
                    foreach (PreparedSubmissionPhoto photo in prepared)
                    {
                        photo.Metadata.PhotoCrossStreet = crossStreet;
                    }
                }
                SubmissionReceipt receipt =
                    await reportStore.CommitAsync(id, DeviceId, intent, prepared, cancellationToken);
                try
                {
                    await slackbotProvider.SendSlackMessage(
                        $"New submission. {first.NumberOfCars} car(s) @ {prepared[0].Metadata.PhotoCrossStreet}");
                }
                catch (Exception)
                {
                    logger.LogWarning("A report was accepted but its Slack notification failed.");
                }
                return Ok(receipt);
            }
            catch (CredentialRejectedException)
            {
                return RejectedCredential("The Mastodon credential is no longer valid.");
            }
            catch (PreparationExpiredException)
            {
                return PreparationExpired();
            }
            catch (UnauthorizedAccessException)
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (ReportInProgressException ex)
            {
                return InProgress(ex);
            }
            catch (BikeLaneException)
            {
                return BadRequest("Prepared photo metadata exceeds the size limit.");
            }
            catch (Exception ex) when (IsUnavailable(ex, cancellationToken))
            {
                return Unavailable("Report submission is temporarily unavailable. Please retry with the same report ID.");
            }
        }

        private async Task<InitialPhotoUploadMetadata> ReadPreparationAsync(string photoId, CancellationToken cancellationToken)
        {
            const int maxMetadataBytes = 64 * 1024;
            BlobClient blob = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{photoId}.json");
            BlobProperties properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            if (properties.ContentLength is <= 0 or > maxMetadataBytes)
            {
                throw new InvalidDataException("Invalid stored metadata size.");
            }
            var download = await blob.DownloadStreamingAsync(new BlobDownloadOptions()
            {
                Conditions = new BlobRequestConditions() { IfMatch = properties.ETag }
            }, cancellationToken);
            using var source = download.Value.Content;
            using var content = await ReadBoundedAsync(source, maxMetadataBytes, cancellationToken);
            if (content.Length != properties.ContentLength)
            {
                throw new InvalidDataException("Incomplete stored metadata.");
            }
            return await JsonSerializer.DeserializeAsync<InitialPhotoUploadMetadata>(content,
                cancellationToken: cancellationToken) ?? throw new InvalidDataException("Missing stored metadata.");
        }

        private bool ValidReportFields(FinalizedPhotoUpload photo, out double latitude, out double longitude)
        {
            latitude = longitude = 0;
            return photo.PhotoDateTime is { } date && date != DateTime.MinValue &&
                photo.NumberOfCars is >= 1 && photo.PhotoCrossStreet?.Length is not > 1024 &&
                double.TryParse(photo.PhotoLatitude, NumberStyles.Float, CultureInfo.InvariantCulture, out latitude) &&
                double.TryParse(photo.PhotoLongitude, NumberStyles.Float, CultureInfo.InvariantCulture, out longitude) &&
                double.IsFinite(latitude) && double.IsFinite(longitude) &&
                SeattleBoundingBox.Contains(new Position(longitude, latitude));
        }

        private static bool SameReportFields(FinalizedPhotoUpload first, FinalizedPhotoUpload next)
        {
            return next.PhotoDateTime == first.PhotoDateTime && next.PhotoLatitude == first.PhotoLatitude &&
                next.PhotoLongitude == first.PhotoLongitude && next.NumberOfCars == first.NumberOfCars &&
                next.PhotoCrossStreet == first.PhotoCrossStreet &&
                next.UserSpecifiedDateTime == first.UserSpecifiedDateTime &&
                next.UserSpecifiedLocation == first.UserSpecifiedLocation;
        }

        private static bool ValidPreparationId(string? id)
        {
            return id is { Length: > 0 and <= 100 } &&
                id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        }

        private static ReportAttribution NormalizeAttribution(ReportAttribution value)
        {
            string? did = value.BlueskyDid?.Trim();
            string? account = value.MastodonAccountId?.Trim();
            if (did is not null && (did.Length is 0 or > 512 ||
                    !did.StartsWith("did:", StringComparison.Ordinal) || did.Any(char.IsWhiteSpace)) ||
                account is { Length: 0 or > 128 } ||
                (value.MastodonServer is null) != (account is null))
            {
                throw new ArgumentException("Invalid attribution.");
            }
            return new ReportAttribution(did, value.MastodonServer is null ? null :
                MastodonCredentialVerifier.NormalizeServer(value.MastodonServer.Trim()), account);
        }

        private IActionResult PreparationExpired()
        {
            return StatusCode(StatusCodes.Status410Gone, new UploadError(UploadErrors.PreparationExpired,
                "The prepared photos expired or changed. Upload the photos again using the same report ID."));
        }

        private static bool IsMissingOrChangedPreparation(RequestFailedException exception)
        {
            return exception.Status == 404 && exception.ErrorCode is null or "BlobNotFound" ||
                exception.Status == 412 && exception.ErrorCode is null or "ConditionNotMet";
        }

        private IActionResult RejectedCredential(string message)
        {
            return Unauthorized(new UploadError(UploadErrors.CredentialRejected, message));
        }

        private IActionResult InProgress(ReportInProgressException exception)
        {
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(exception.RetryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            return Conflict(new UploadError(UploadErrors.ReportInProgress,
                "This report is being accepted. Check its receipt and retry shortly."));
        }

        private IActionResult Unavailable(string message)
        {
            logger.LogWarning("An upload dependency is temporarily unavailable.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new UploadError(UploadErrors.ProviderUnavailable, message));
        }

        private static bool IsUnavailable(Exception exception, CancellationToken cancellationToken)
        {
            return exception is RequestFailedException or IOException or InvalidDataException or JsonException or HttpRequestException or
                    ProviderUnavailableException or TimeoutException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }

        private sealed class ValidateFinalizationAfterReceiptAttribute : ActionFilterAttribute
        {
            public ValidateFinalizationAfterReceiptAttribute()
            {
                Order = -3000;
            }

            public override void OnActionExecuting(ActionExecutingContext context)
            {
                // Keep JSON binding errors (including legacy arrays), but defer field validation until
                // after receipt recovery. The action validates every client-writable report field.
                if (context.ActionArguments.TryGetValue("request", out object? request) &&
                    request is FinalizeReportRequest)
                {
                    context.ModelState.Clear();
                }
            }
        }
    }
}
