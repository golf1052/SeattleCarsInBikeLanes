using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

/// <summary>
/// The when and where of a photo, which is all this needs to know about one.
/// </summary>
/// <remarks>
/// An interface rather than a record the caller has to convert into, so the selector hands back the
/// caller's own photos and there is nothing to match up afterwards.
/// </remarks>
public interface IPhotoMoment
{
    string Id { get; }

    DateTimeOffset? CreatedAt { get; }

    GeoPosition? Location { get; }
}

/// <summary>
/// What counts as recent, and what counts as the same incident.
/// </summary>
public sealed record RecentPhotoRules
{
    /// <summary>
    /// How long after a photo is taken it is still the one the user came here to report.
    /// </summary>
    /// <remarks>
    /// Long enough to cover finishing the block, stopping, and getting the phone back out. Beyond
    /// this the user is reporting something they thought about first, and guessing at their
    /// selection stops being helpful.
    /// </remarks>
    public static readonly TimeSpan DefaultRecencyWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far apart two photos of the same thing can be.
    /// </summary>
    /// <remarks>
    /// About half a Seattle block, which holds several photos of one car together while keeping
    /// them apart from the next thing seen down the street. Deliberately tight: a phone's fix in an
    /// urban canyon is routinely out by ten or twenty metres, so a second photo of the same car can
    /// occasionally fall outside this and be left unticked, which is the friction that already
    /// exists. Quietly attaching an unrelated photo to somebody's report is the worse mistake.
    /// </remarks>
    public const double DefaultMaxDistanceMeters = 50d;

    /// <summary>
    /// How far apart in time two photos can be when there is no location to go on.
    /// </summary>
    /// <remarks>
    /// Short, because time on its own is weak evidence: somebody riding along passes a different
    /// place every minute, and the whole point of this is not to gather up two unrelated things.
    /// </remarks>
    public static readonly TimeSpan DefaultMaxTimeGap = TimeSpan.FromMinutes(2);

    public TimeSpan RecencyWindow { get; init; } = DefaultRecencyWindow;

    public double MaxDistanceMeters { get; init; } = DefaultMaxDistanceMeters;

    public TimeSpan MaxTimeGap { get; init; } = DefaultMaxTimeGap;

    /// <summary>
    /// How many photos may be picked, which is however many the site will take in one report.
    /// </summary>
    public int MaxPhotos { get; init; } = 4;

    public static RecentPhotoRules ForReport(int maxPhotos) => new RecentPhotoRules()
    {
        MaxPhotos = maxPhotos
    };
}

/// <summary>
/// Works out which photos belong to the thing the user has just photographed.
/// </summary>
/// <remarks>
/// The app is used one handed, outdoors, usually while getting off a bike, and the photo that was
/// just taken is nearly always the one being reported. Picking it out saves the user hunting for
/// their own photo in a roll of photos that all look alike.
/// </remarks>
public static class RecentPhotoSelector
{
    /// <summary>
    /// Whether a photo is new enough to count as just taken.
    /// </summary>
    /// <remarks>
    /// A photo with no timestamp is never recent. There is nothing to judge it by, and the roll is
    /// full of imported photos that could be from any day at all.
    ///
    /// The window is applied in both directions so a clock that is a little ahead, or a photo whose
    /// timestamp came from a camera set to the wrong time zone, does not leave the user staring at
    /// a photo the app refuses to treat as new.
    /// </remarks>
    public static bool IsRecent(IPhotoMoment photo, DateTimeOffset now, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(photo);

        if (photo.CreatedAt is not DateTimeOffset createdAt)
        {
            return false;
        }

        TimeSpan age = now - createdAt;
        return age <= window && age >= -window;
    }

    /// <summary>
    /// Whether a photo is part of the same thing being photographed as another.
    /// </summary>
    /// <remarks>
    /// Location first, because it is the stronger evidence by far, and time only when one of the
    /// two has no location to compare. Time is symmetric here so the question can be asked in
    /// either direction.
    /// </remarks>
    public static bool BelongsWith(IPhotoMoment anchor, IPhotoMoment candidate, RecentPhotoRules rules)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rules);

        return BelongsWith(anchor, anchor, candidate, rules);
    }

    private static bool BelongsWith(IPhotoMoment anchor,
        IPhotoMoment previous,
        IPhotoMoment candidate,
        RecentPhotoRules rules)
    {
        if (anchor.Location is GeoPosition anchorLocation &&
            candidate.Location is GeoPosition candidateLocation)
        {
            return anchorLocation.DistanceInMetersTo(candidateLocation) <= rules.MaxDistanceMeters;
        }

        // No location on one of them, so time is all there is, and it has to be held to a much
        // shorter leash than the recency window to mean anything.
        if (previous.CreatedAt is not DateTimeOffset previousTakenAt ||
            candidate.CreatedAt is not DateTimeOffset candidateTakenAt)
        {
            return false;
        }

        return (previousTakenAt - candidateTakenAt).Duration() <= rules.MaxTimeGap;
    }

    /// <summary>
    /// Picks the newest photo and whichever of the ones before it belong with it.
    /// </summary>
    /// <remarks>
    /// Walks back in time from the newest recent photo and stops at the first one that does not
    /// belong, rather than carrying on to look for more. A break in the run is the user having
    /// moved on: two photos five minutes and a few blocks apart are two different reports, and
    /// stepping over the gap to collect the older one is how they would end up in the same report
    /// without the user noticing.
    ///
    /// Distance is measured against the anchor rather than the previous photo, so walking around a
    /// car taking several photos cannot creep away from where it started.
    /// </remarks>
    /// <param name="photos">The photos worth considering, in any order.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The photos to select, newest first, which is empty when nothing is recent.</returns>
    public static IReadOnlyList<T> SelectCluster<T>(IReadOnlyList<T> photos,
        DateTimeOffset now,
        RecentPhotoRules rules)
        where T : IPhotoMoment
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.MaxPhotos <= 0)
        {
            return Array.Empty<T>();
        }

        List<T> ordered = photos
            .Where(photo => photo.CreatedAt.HasValue)
            .OrderByDescending(photo => photo.CreatedAt!.Value)
            .ToList();

        // Not simply the newest photo: a timestamp far enough in the future to fall outside the
        // window would otherwise sit at the top and hide the genuinely recent photos behind it.
        int anchorIndex = ordered.FindIndex(photo => IsRecent(photo, now, rules.RecencyWindow));
        if (anchorIndex < 0)
        {
            return Array.Empty<T>();
        }

        T anchor = ordered[anchorIndex];
        List<T> cluster = new List<T>(rules.MaxPhotos) { anchor };
        T previous = anchor;

        for (int i = anchorIndex + 1; i < ordered.Count && cluster.Count < rules.MaxPhotos; i++)
        {
            T candidate = ordered[i];

            if (!IsRecent(candidate, now, rules.RecencyWindow))
            {
                break;
            }

            if (!BelongsWith(anchor, previous, candidate, rules))
            {
                break;
            }

            cluster.Add(candidate);
            previous = candidate;
        }

        return cluster;
    }
}
