namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

public interface IReportedPhoto
{
    string Id { get; }

    bool Submitted { get; }

    string? SubmissionId { get; }

    DateTimeOffset? SubmittedAt { get; }

    DateTimeOffset? CreatedAt { get; }
}

public sealed record ReportedPhotoGroup<T>(
    string? SubmissionId,
    DateTimeOffset? SubmittedAt,
    IReadOnlyList<T> Photos);

public static class ReportedPhotoGrouper
{
    public static IReadOnlyList<ReportedPhotoGroup<T>> Group<T>(IEnumerable<T> photos)
        where T : IReportedPhoto
    {
        ArgumentNullException.ThrowIfNull(photos);

        Dictionary<string, List<T>> reports = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        List<ReportedPhotoGroup<T>> groups = new List<ReportedPhotoGroup<T>>();

        foreach (T photo in photos)
        {
            if (!photo.Submitted)
            {
                continue;
            }

            string? submissionId = photo.SubmissionId;
            if (string.IsNullOrWhiteSpace(submissionId))
            {
                // Missing report identity cannot safely associate this photo with any other photo.
                groups.Add(new ReportedPhotoGroup<T>(null, photo.SubmittedAt, new[] { photo }));
                continue;
            }

            if (!reports.TryGetValue(submissionId, out List<T>? members))
            {
                members = new List<T>();
                reports.Add(submissionId, members);
            }

            members.Add(photo);
        }

        foreach (KeyValuePair<string, List<T>> report in reports)
        {
            T[] members = report.Value
                .OrderByDescending(photo => photo.CreatedAt)
                .ThenBy(photo => photo.Id, StringComparer.Ordinal)
                .ToArray();

            groups.Add(new ReportedPhotoGroup<T>(
                report.Key,
                members.Max(photo => photo.SubmittedAt),
                members));
        }

        return groups
            .OrderByDescending(group => group.SubmittedAt)
            .ThenBy(group => group.SubmissionId is null)
            .ThenBy(group => group.SubmissionId, StringComparer.Ordinal)
            .ThenBy(group => group.Photos[0].Id, StringComparer.Ordinal)
            .ToArray();
    }
}
