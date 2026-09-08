using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class RecentPhotoSelectorTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.FromHours(-7));

    private static readonly GeoPosition BikeLane = new GeoPosition(47.6205, -122.3493);

    private sealed record TestPhoto(string Id, DateTimeOffset? CreatedAt, GeoPosition? Location) : IPhotoMoment;

    /// <summary>
    /// A photo taken so many seconds ago, at the bike lane unless moved.
    /// </summary>
    private static TestPhoto Photo(string id, double secondsAgo, double? metersAway = 0d) => new TestPhoto(
        id,
        Now.AddSeconds(-secondsAgo),
        metersAway is double meters ? North(BikeLane, meters) : null);

    /// <summary>
    /// A position a given number of metres north, which is the one direction where a degree is the
    /// same length wherever you are.
    /// </summary>
    private static GeoPosition North(GeoPosition origin, double meters) =>
        origin with { Latitude = origin.Latitude + (meters / 111195d) };

    private static IReadOnlyList<TestPhoto> Select(params TestPhoto[] photos) =>
        RecentPhotoSelector.SelectCluster(photos, Now, RecentPhotoRules.ForReport(4));

    [Fact]
    public void SelectsNothingFromAnEmptyRoll()
    {
        Assert.Empty(Select());
    }

    [Fact]
    public void SelectsNothingWhenTheNewestPhotoIsNotRecent()
    {
        Assert.Empty(Select(Photo("old", secondsAgo: 20 * 60), Photo("older", secondsAgo: 21 * 60)));
    }

    [Fact]
    public void SelectsAPhotoJustTaken()
    {
        IReadOnlyList<TestPhoto> selected = Select(Photo("just taken", secondsAgo: 5));

        Assert.Equal(new[] { "just taken" }, selected.Select(photo => photo.Id));
    }

    [Fact]
    public void SelectsSeveralPhotosOfTheSameThing()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("third", secondsAgo: 10, metersAway: 8),
            Photo("second", secondsAgo: 25, metersAway: 4),
            Photo("first", secondsAgo: 40));

        Assert.Equal(new[] { "third", "second", "first" }, selected.Select(photo => photo.Id));
    }

    /// <summary>
    /// The case this exists for: a photo taken, not reported, and another taken further along the
    /// ride. Both are recent, and they are nothing to do with each other.
    /// </summary>
    [Fact]
    public void LeavesBehindAPhotoFromFurtherBackAlongTheRide()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("here", secondsAgo: 30),
            Photo("five minutes back", secondsAgo: 5 * 60, metersAway: 400));

        Assert.Equal(new[] { "here" }, selected.Select(photo => photo.Id));
    }

    [Fact]
    public void LeavesBehindAPhotoWithNoLocationTakenMinutesEarlier()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("here", secondsAgo: 30),
            Photo("five minutes back", secondsAgo: 5 * 60, metersAway: null));

        Assert.Equal(new[] { "here" }, selected.Select(photo => photo.Id));
    }

    [Fact]
    public void KeepsAPhotoWithNoLocationTakenMomentsEarlier()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("here", secondsAgo: 30),
            Photo("moments earlier", secondsAgo: 75, metersAway: null));

        Assert.Equal(new[] { "here", "moments earlier" }, selected.Select(photo => photo.Id));
    }

    /// <summary>
    /// The gap is measured from the last photo taken into the cluster, not from the newest one, so
    /// a steady run of photos with no location stays together.
    /// </summary>
    [Fact]
    public void FollowsARunOfPhotosWithNoLocation()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("third", secondsAgo: 10, metersAway: null),
            Photo("second", secondsAgo: 100, metersAway: null),
            Photo("first", secondsAgo: 190, metersAway: null));

        Assert.Equal(new[] { "third", "second", "first" }, selected.Select(photo => photo.Id));
    }

    [Fact]
    public void TakesTheMostRecentFourWhenThereAreMore()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("sixth", secondsAgo: 10),
            Photo("fifth", secondsAgo: 20),
            Photo("fourth", secondsAgo: 30),
            Photo("third", secondsAgo: 40),
            Photo("second", secondsAgo: 50),
            Photo("first", secondsAgo: 60));

        Assert.Equal(new[] { "sixth", "fifth", "fourth", "third" }, selected.Select(photo => photo.Id));
    }

    [Fact]
    public void StopsAtTheEdgeOfTheRecencyWindow()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("inside", secondsAgo: 14 * 60),
            Photo("outside", secondsAgo: 16 * 60));

        Assert.Equal(new[] { "inside" }, selected.Select(photo => photo.Id));
    }

    /// <summary>
    /// Imported photos routinely have no timestamp the app can read, and there is no way to tell
    /// whether one of those is from this minute or last year.
    /// </summary>
    [Fact]
    public void IgnoresPhotosWithNoTimestamp()
    {
        TestPhoto undated = new TestPhoto("undated", null, BikeLane);

        IReadOnlyList<TestPhoto> selected = Select(undated, Photo("just taken", secondsAgo: 5));

        Assert.Equal(new[] { "just taken" }, selected.Select(photo => photo.Id));
    }

    /// <summary>
    /// A photo dated well into the future would otherwise sit at the top of the roll and hide the
    /// photo the user actually just took.
    /// </summary>
    [Fact]
    public void LooksPastATimestampFarInTheFuture()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("wrong clock", secondsAgo: -60 * 60),
            Photo("just taken", secondsAgo: 5),
            Photo("moments earlier", secondsAgo: 20));

        Assert.Equal(new[] { "just taken", "moments earlier" }, selected.Select(photo => photo.Id));
    }

    [Fact]
    public void DoesNotCareWhatOrderTheRollComesIn()
    {
        IReadOnlyList<TestPhoto> selected = Select(
            Photo("first", secondsAgo: 40),
            Photo("third", secondsAgo: 10),
            Photo("second", secondsAgo: 25));

        Assert.Equal(new[] { "third", "second", "first" }, selected.Select(photo => photo.Id));
    }

    [Fact]
    public void SelectsNothingWhenNoPhotosAreAllowed()
    {
        IReadOnlyList<TestPhoto> selected = RecentPhotoSelector.SelectCluster(
            new[] { Photo("just taken", secondsAgo: 5) },
            Now,
            RecentPhotoRules.ForReport(0));

        Assert.Empty(selected);
    }

    [Fact]
    public void CountsAPhotoWithNoTimestampAsNotRecent()
    {
        TestPhoto undated = new TestPhoto("undated", null, BikeLane);

        Assert.False(RecentPhotoSelector.IsRecent(undated, Now, TimeSpan.FromMinutes(15)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(14 * 60, true)]
    [InlineData(16 * 60, false)]
    [InlineData(-60, true)]
    public void JudgesRecencyAgainstTheWindow(double secondsAgo, bool expected)
    {
        Assert.Equal(expected,
            RecentPhotoSelector.IsRecent(Photo("photo", secondsAgo), Now, TimeSpan.FromMinutes(15)));
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(49, true)]
    [InlineData(51, false)]
    [InlineData(400, false)]
    public void JudgesTwoPlacedPhotosOnDistanceAlone(double metersApart, bool expected)
    {
        // Ten minutes apart, which the distance is allowed to override either way.
        TestPhoto anchor = Photo("anchor", secondsAgo: 0);
        TestPhoto other = Photo("other", secondsAgo: 10 * 60, metersAway: metersApart);

        Assert.Equal(expected, RecentPhotoSelector.BelongsWith(anchor, other, RecentPhotoRules.ForReport(4)));
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(180, false)]
    public void FallsBackToTimeWhenAPhotoHasNoLocation(double secondsApart, bool expected)
    {
        TestPhoto anchor = Photo("anchor", secondsAgo: 0);
        TestPhoto other = Photo("other", secondsAgo: secondsApart, metersAway: null);

        Assert.Equal(expected, RecentPhotoSelector.BelongsWith(anchor, other, RecentPhotoRules.ForReport(4)));
    }

    /// <summary>
    /// Asked about an already sent photo, the question runs backwards as often as forwards.
    /// </summary>
    [Fact]
    public void AnswersTheSameWhicheverPhotoIsAskedAbout()
    {
        TestPhoto older = Photo("older", secondsAgo: 90, metersAway: null);
        TestPhoto newer = Photo("newer", secondsAgo: 0, metersAway: null);
        RecentPhotoRules rules = RecentPhotoRules.ForReport(4);

        Assert.True(RecentPhotoSelector.BelongsWith(newer, older, rules));
        Assert.True(RecentPhotoSelector.BelongsWith(older, newer, rules));
    }
}
