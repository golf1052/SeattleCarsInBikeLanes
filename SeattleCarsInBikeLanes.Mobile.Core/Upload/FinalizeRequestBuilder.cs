using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Core.Upload;

/// <summary>
/// The identity a report can be credited to.
/// </summary>
/// <remarks>
/// Bluesky is deliberately just a handle. The server takes the Bluesky identity from the
/// authenticated session and ignores anything the client sends, so the handle here is only used to
/// build the display string and to know whether attribution is possible at all.
///
/// Mastodon works the other way around: the site has no Mastodon session, so the access token
/// travels with the report and the server verifies it.
/// </remarks>
public sealed class AttributionIdentity
{
    public string? BlueskyHandle { get; init; }

    public string? MastodonUsername { get; init; }

    public string? MastodonFullUsername { get; init; }

    public string? MastodonEndpoint { get; init; }

    public string? MastodonAccessToken { get; init; }

    public bool HasBluesky => !string.IsNullOrWhiteSpace(BlueskyHandle);

    public bool HasMastodon =>
        !string.IsNullOrWhiteSpace(MastodonAccessToken) &&
        !string.IsNullOrWhiteSpace(MastodonEndpoint) &&
        !string.IsNullOrWhiteSpace(MastodonFullUsername);

    public bool CanAttribute => HasBluesky || HasMastodon;

    /// <summary>
    /// What the user sees when deciding whether to be credited.
    /// </summary>
    public string? DisplayName => HasBluesky ? BlueskyHandle : MastodonFullUsername;
}

/// <summary>
/// Turns the server's response to the initial upload plus the user's report into the finalize body.
/// </summary>
public static class FinalizeRequestBuilder
{
    /// <summary>
    /// What the server writes when a report is not credited to anyone.
    /// </summary>
    public const string AnonymousSubmittedBy = "Submission";

    public static List<FinalizedPhotoUpload> Build(IReadOnlyList<InitialPhotoUpload> photos,
        ReportDraft draft,
        AttributionIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(draft);

        if (photos.Count == 0)
        {
            throw new ArgumentException("A report needs at least one photo.", nameof(photos));
        }

        bool attribute = draft.Attribute && identity is not null && identity.CanAttribute;

        List<FinalizedPhotoUpload> result = new List<FinalizedPhotoUpload>(photos.Count);
        foreach (InitialPhotoUpload photo in photos)
        {
            FinalizedPhotoUpload finalized = new FinalizedPhotoUpload()
            {
                PhotoId = photo.PhotoId,
                SubmissionId = photo.SubmissionId,
                PhotoNumber = photo.PhotoNumber,
                Tags = photo.Tags,
                NumberOfCars = draft.NumberOfCars,
                PhotoDateTime = draft.TakenAt ?? photo.PhotoDateTime,
                UserSpecifiedDateTime = draft.UserSpecifiedDateTime,
                UserSpecifiedLocation = draft.UserSpecifiedLocation,

                // A blank cross street tells the server to reverse geocode one itself, which is
                // what we want whenever the user has moved the pin.
                PhotoCrossStreet = draft.UserSpecifiedLocation ? null : photo.PhotoCrossStreet,

                TwitterSubmittedBy = AnonymousSubmittedBy,
                MastodonSubmittedBy = AnonymousSubmittedBy,
                BlueskySubmittedBy = AnonymousSubmittedBy
            };

            if (draft.Location is GeoPosition location)
            {
                finalized.PhotoLatitude = location.LatitudeString;
                finalized.PhotoLongitude = location.LongitudeString;
            }
            else
            {
                finalized.PhotoLatitude = photo.PhotoLatitude;
                finalized.PhotoLongitude = photo.PhotoLongitude;
            }

            if (attribute && identity is not null)
            {
                finalized.Attribute = true;

                if (identity.HasBluesky)
                {
                    finalized.BlueskySubmittedBy = $"Submitted by {identity.BlueskyHandle}";
                }

                if (identity.HasMastodon)
                {
                    finalized.MastodonSubmittedBy = $"Submitted by {identity.MastodonFullUsername}";
                    finalized.MastodonUsername = identity.MastodonUsername;
                    finalized.MastodonFullUsername = identity.MastodonFullUsername;
                    finalized.MastodonEndpoint = identity.MastodonEndpoint;
                    finalized.MastodonAccessToken = identity.MastodonAccessToken;
                }
            }

            result.Add(finalized);
        }

        return result;
    }
}
