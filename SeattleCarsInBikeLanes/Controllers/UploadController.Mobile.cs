using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Controllers;

public partial class UploadController
{
    [HttpGet("Reports/{id}")]
    public async Task<IActionResult> ReportStatus(string id, CancellationToken cancellationToken)
    {
        if (!SubmissionClaimProvider.IsValidReportId(id) || string.IsNullOrWhiteSpace(DeviceId))
        {
            return BadRequest("A valid report and device ID are required.");
        }
        try
        {
            SubmissionReport? report = await submissionClaimProvider.GetAsync(id, DeviceId, cancellationToken);
            return report is null ? NotFound() : Ok(report.Receipt);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (Exception ex) when (ex is RequestFailedException or IOException or System.Text.Json.JsonException)
        {
            logger.LogError(ex, "Could not read mobile report {ReportId}.", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost("FinalizeMobile")]
    public async Task<IActionResult> FinalizeMobile([FromBody] MobileFinalizeRequest request,
        [FromServices] MastodonCredentialVerifier mastodonVerifier,
        CancellationToken cancellationToken)
    {
        if (ReportId is not { } id || string.IsNullOrWhiteSpace(DeviceId))
        {
            return BadRequest("A valid report and device ID are required.");
        }
        try
        {
            // Reconcile before checking now-expired credentials or newly prepared photo IDs.
            SubmissionReport? accepted = await submissionClaimProvider.GetAsync(id, DeviceId, cancellationToken);
            if (accepted is not null)
            {
                return Ok(accepted.Receipt);
            }
            if (await deviceBlocklistProvider.IsBlocked(DeviceId))
            {
                return StatusCode(StatusCodes.Status403Forbidden, "This device can't submit reports.");
            }
            if (request.Photos is not { Count: > 0 and <= MaxPhotosPerReport } ||
                request.Attribution is null || request.Photos.Any(p => p is null))
            {
                return BadRequest("A report needs one to four photos and explicit attribution.");
            }
            FinalizedPhotoUpload first = request.Photos[0];
            if (first.PhotoDateTime is null || first.NumberOfCars is not >= 1 ||
                first.PhotoCrossStreet?.Length > 1024 ||
                !double.TryParse(first.PhotoLongitude, CultureInfo.InvariantCulture, out double longitude) ||
                !double.TryParse(first.PhotoLatitude, CultureInfo.InvariantCulture, out double latitude) ||
                !double.IsFinite(longitude) || !double.IsFinite(latitude) ||
                !SeattleBoundingBox.Contains(new Position(longitude, latitude)))
            {
                return BadRequest("A report needs a date, a Seattle location, and at least one car.");
            }

            string? blueskyHandle = null;
            if (request.Attribution.BlueskyDid is { } intendedDid)
            {
                AuthenticateResult auth = await HttpContext.AuthenticateAsync(BlueskyAuthDefaults.BearerScheme);
                if (!auth.Succeeded)
                {
                    return Unauthorized(new MobileUploadError(MobileUploadErrors.CredentialRejected,
                        "The retained Bluesky credential is no longer valid."));
                }
                if (auth.Principal.FindFirstValue(BlueskyAuthDefaults.DidClaim) != intendedDid)
                {
                    return Conflict(new MobileUploadError(MobileUploadErrors.IdentityMismatch,
                        "The Bluesky credential does not match the queued account."));
                }
                blueskyHandle = auth.Principal.FindFirstValue(BlueskyAuthDefaults.HandleClaim);
                if (string.IsNullOrWhiteSpace(blueskyHandle))
                {
                    return Unauthorized(new MobileUploadError(MobileUploadErrors.CredentialRejected,
                        "The retained Bluesky credential has no identity."));
                }
            }

            VerifiedMastodonAccount? mastodon = null;
            if (request.Attribution.MastodonAccountId is not null || request.Attribution.MastodonServer is not null)
            {
                if (request.Attribution.MastodonAccountId is null || request.Attribution.MastodonServer is null ||
                    string.IsNullOrWhiteSpace(first.MastodonAccessToken))
                {
                    return BadRequest("The selected Mastodon account requires its retained credential.");
                }
                mastodon = await mastodonVerifier.VerifyAsync(request.Attribution.MastodonServer,
                    first.MastodonAccessToken, cancellationToken);
                if (mastodon.Id != request.Attribution.MastodonAccountId)
                {
                    return Conflict(new MobileUploadError(MobileUploadErrors.IdentityMismatch,
                        "The Mastodon credential does not match the queued account."));
                }
            }

            string? crossStreet = first.PhotoCrossStreet;
            if (string.IsNullOrWhiteSpace(crossStreet))
            {
                crossStreet = (await helperMethods.ReverseSearchCrossStreet(
                    new Position(longitude, latitude), mapsSearchClient))?.Address.StreetName;
            }

            List<SubmissionPhoto> photos = [];
            HashSet<string> initialIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < request.Photos.Count; i++)
            {
                FinalizedPhotoUpload supplied = request.Photos[i];
                if (string.IsNullOrEmpty(supplied.PhotoId) || supplied.PhotoId.Length > 100 ||
                    supplied.PhotoId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
                    !initialIds.Add(supplied.PhotoId) || supplied.PhotoNumber != i ||
                    supplied.SubmissionId != first.SubmissionId)
                {
                    return BadRequest("Invalid prepared photo set.");
                }
                BlobClient source = blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{supplied.PhotoId}.jpeg");
                var sourceProperties = await source.GetPropertiesAsync(cancellationToken: cancellationToken);
                if (sourceProperties.Value.ContentLength is <= 0 or > SubmissionClaimProvider.MaxPhotoBytes)
                {
                    return BadRequest("A prepared photo exceeds the mobile upload limit.");
                }
                var initial = await blobContainerClient.GetBlobClient($"{InitialUploadPrefix}{supplied.PhotoId}.json")
                    .DownloadContentAsync(cancellationToken);
                InitialPhotoUploadMetadata stored = initial.Value.Content.ToObjectFromJson<InitialPhotoUploadMetadata>()
                    ?? throw new InvalidDataException("Initial photo metadata is empty.");
                if (stored.PhotoId != supplied.PhotoId || stored.PhotoNumber != i ||
                    stored.SubmissionId != first.SubmissionId)
                {
                    return BadRequest("The prepared photos do not belong to the same report.");
                }
                // The source is immutable; pin the content download to its checked version.
                var download = await source.DownloadContentAsync(new Azure.Storage.Blobs.Models.BlobDownloadOptions
                {
                    Conditions = new Azure.Storage.Blobs.Models.BlobRequestConditions { IfMatch = sourceProperties.Value.ETag }
                }, cancellationToken);
                byte[] jpeg = download.Value.Content.ToArray();
                FinalizedPhotoUploadMetadata metadata = FinalizedPhotoUploadMetadata.FromContract(first);
                metadata.PhotoId = $"{id}-{i}";
                metadata.PhotoNumber = i;
                metadata.SubmissionId = id;
                metadata.ReportId = id;
                metadata.DeviceId = DeviceId;
                metadata.Tags = stored.Tags;
                metadata.PhotoLatitude = latitude.ToString("G", CultureInfo.InvariantCulture);
                metadata.PhotoLongitude = longitude.ToString("G", CultureInfo.InvariantCulture);
                metadata.PhotoCrossStreet = crossStreet;
                metadata.Attribute = !request.Attribution.IsAnonymous;
                metadata.TwitterAccessToken = metadata.MastodonAccessToken = metadata.ThreadsAccessToken = null;
                metadata.TwitterUsername = metadata.ThreadsUsername = null;
                metadata.TwitterSubmittedBy = metadata.ThreadsSubmittedBy = "Submission";
                metadata.BlueskyHandle = blueskyHandle;
                metadata.BlueskyUserDid = request.Attribution.BlueskyDid;
                metadata.BlueskySubmittedBy = blueskyHandle is null ? "Submission" : $"Submitted by {blueskyHandle}";
                metadata.MastodonEndpoint = mastodon?.Server;
                metadata.MastodonUsername = mastodon?.Username;
                metadata.MastodonFullUsername = mastodon?.FullUsername;
                metadata.MastodonSubmittedBy = mastodon is null ? "Submission" : $"Submitted by {mastodon.FullUsername}";
                photos.Add(new SubmissionPhoto(metadata, jpeg, Convert.ToHexString(SHA256.HashData(jpeg))));
            }

            SubmissionReceipt proposed = new SubmissionReceipt(id, id, DateTimeOffset.UtcNow, request.Attribution);
            SubmissionReceipt receipt = await submissionClaimProvider.CommitAsync(new SubmissionReport(
                proposed,
                DeviceId!, photos), cancellationToken);
            if (receipt == proposed)
            {
                await slackbotProvider.SendSlackMessage(
                    $"New submission. {first.NumberOfCars} car(s) @ {crossStreet} from device {DeviceId}");
            }
            return Ok(receipt);
        }
        catch (CredentialRejectedException)
        {
            return Unauthorized(new MobileUploadError(MobileUploadErrors.CredentialRejected,
                "The retained Mastodon credential is no longer valid."));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (Exception ex) when (ex is ProviderUnavailableException or HttpRequestException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("A provider could not verify attribution for report {ReportId}.", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new MobileUploadError(MobileUploadErrors.ProviderUnavailable, "Account verification is temporarily unavailable."));
        }
        catch (Exception ex) when (ex is RequestFailedException or IOException or System.Text.Json.JsonException)
        {
            logger.LogError(ex, "Could not finalize mobile report {ReportId}.", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
