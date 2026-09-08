using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class PhotoRollLayoutTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 110)]
    [InlineData(3, 110)]
    [InlineData(4, 224)]
    public void GridHeightAccountsForWrapping(int count, double expected)
    {
        Assert.Equal(expected, PhotoRollLayout.PhotoGridHeight(count));
    }

    [Fact]
    public void EmptyRollHasNoPinnedHeight()
    {
        Assert.Equal((0d, 0d), PhotoRollLayout.Measure(0, Array.Empty<int>(), true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void OneReportIncludesItsHeaderAndSpacing(int count)
    {
        (double recent, double reported) = PhotoRollLayout.Measure(0, new[] { count }, true);

        Assert.Equal(0, recent);
        Assert.Equal(PhotoRollLayout.ReportHeaderHeight + PhotoRollLayout.ThumbnailHeight +
            PhotoRollLayout.ReportFooterHeight, reported);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(1, 1)]
    [InlineData(4, 0)]
    public void ReportsStartNewRowsAndStayWithinTheCap(int first, int second)
    {
        (double recent, double reported) = PhotoRollLayout.Measure(0, new[] { first, second }, true);

        Assert.Equal(0, recent);
        Assert.Equal(PhotoRollLayout.MaxPinnedHeight, reported);
    }

    [Fact]
    public void CollapsedHistoryGivesItsBudgetBackToRecentPhotos()
    {
        (double recent, double reported) = PhotoRollLayout.Measure(9, new[] { 1, 3 }, false);

        Assert.Equal(PhotoRollLayout.MaxPinnedHeight, recent);
        Assert.Equal(0, reported);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(30)]
    public void RecentAndReportedPhotosShareOneHeightBudget(int recentCount)
    {
        (double recent, double reported) = PhotoRollLayout.Measure(recentCount, new[] { 1, 3 }, true);

        Assert.Equal(PhotoRollLayout.ThumbnailHeight, recent);
        Assert.Equal(PhotoRollLayout.MaxPinnedHeight - recent, reported);
        Assert.True(recent + reported <= PhotoRollLayout.MaxPinnedHeight);
    }

    [Fact]
    public void RemovingTheLastReportRestoresRecentHeight()
    {
        Assert.Equal((PhotoRollLayout.MaxPinnedHeight, 0d),
            PhotoRollLayout.Measure(9, Array.Empty<int>(), true));
    }

    [Fact]
    public void RejectsNegativePhotoCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PhotoRollLayout.PhotoGridHeight(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhotoRollLayout.Measure(0, new[] { -1 }, true));
    }
}
