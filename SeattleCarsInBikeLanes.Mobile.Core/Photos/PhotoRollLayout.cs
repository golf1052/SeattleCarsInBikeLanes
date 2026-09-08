namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

/// <summary>
/// The pinned photo sections share a budget so the pending roll remains reachable.
/// </summary>
public static class PhotoRollLayout
{
    public const int Columns = 3;
    public const double ThumbnailHeight = 110;
    public const double ThumbnailSpacing = 4;
    public const double ReportHeaderHeight = 32;
    public const double ReportFooterHeight = 8;
    public const double MaxPinnedHeight = 244;

    public static double PhotoGridHeight(int photos)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(photos);
        int rows = (photos + Columns - 1) / Columns;
        return (rows * ThumbnailHeight) + (Math.Max(rows - 1, 0) * ThumbnailSpacing);
    }

    public static (double Recent, double Reported) Measure(
        int recentPhotos,
        IEnumerable<int> reportedGroupSizes,
        bool reportedExpanded)
    {
        ArgumentNullException.ThrowIfNull(reportedGroupSizes);
        double recent = PhotoGridHeight(recentPhotos);
        if (!reportedExpanded)
        {
            return (Math.Min(recent, MaxPinnedHeight), 0);
        }

        double reported = 0;
        foreach (int count in reportedGroupSizes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > 0)
            {
                reported += ReportHeaderHeight + PhotoGridHeight(count) + ReportFooterHeight;
            }
        }

        recent = Math.Min(recent, reported > 0 ? ThumbnailHeight : MaxPinnedHeight);
        return (recent, Math.Min(reported, MaxPinnedHeight - recent));
    }
}
