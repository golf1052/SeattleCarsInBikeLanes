using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class ReportedPhotoGrouperTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.FromHours(-7));

    private sealed record TestPhoto(
        string Id,
        bool Submitted,
        string? SubmissionId,
        DateTimeOffset? SubmittedAt,
        DateTimeOffset? CreatedAt) : IReportedPhoto;

    private static TestPhoto Photo(string id, string? submissionId) =>
        new TestPhoto(id, true, submissionId, Now, Now.AddMinutes(-1));

    [Fact]
    public void ReturnsNoGroupsForAnEmptyRoll()
    {
        Assert.Empty(ReportedPhotoGrouper.Group(Array.Empty<TestPhoto>()));
    }

    [Fact]
    public void RejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => ReportedPhotoGrouper.Group<TestPhoto>(null!));
    }

    [Fact]
    public void ExcludesPendingFailedAndUnsubmittedPhotosEvenWithSubmissionMetadata()
    {
        TestPhoto[] photos =
        {
            Photo("pending", "report") with { Submitted = false },
            Photo("failed", "report") with { Submitted = false },
            new TestPhoto("unsubmitted", false, null, null, Now)
        };

        Assert.Empty(ReportedPhotoGrouper.Group(photos));

        TestPhoto submitted = Photo("submitted", "report");
        ReportedPhotoGroup<TestPhoto> group = Assert.Single(
            ReportedPhotoGrouper.Group(photos.Append(submitted)));

        Assert.Same(submitted, Assert.Single(group.Photos));
    }

    [Fact]
    public void SeparatesOnePhotoReportFromThreePhotoReportAndReturnsOriginalPhotos()
    {
        TestPhoto single = Photo("single", "earlier-report") with { SubmittedAt = Now.AddHours(-1) };
        TestPhoto first = Photo("first", "later-report") with { CreatedAt = Now.AddMinutes(-3) };
        TestPhoto second = Photo("second", "later-report") with { CreatedAt = Now.AddMinutes(-2) };
        TestPhoto third = Photo("third", "later-report");

        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> groups =
            ReportedPhotoGrouper.Group(new[] { first, single, third, second });

        Assert.Equal(new[] { "later-report", "earlier-report" }, groups.Select(group => group.SubmissionId));
        Assert.Equal(Now, groups[0].SubmittedAt);
        Assert.Collection(groups[0].Photos,
            photo => Assert.Same(third, photo),
            photo => Assert.Same(second, photo),
            photo => Assert.Same(first, photo));
        Assert.Same(single, Assert.Single(groups[1].Photos));
    }

    [Fact]
    public void DifferentReportIdsStaySeparateWithIdenticalCaptureAndSubmissionTimes()
    {
        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> groups = ReportedPhotoGrouper.Group(new[]
        {
            Photo("second", "report-b"),
            Photo("first", "report-a")
        });

        Assert.Equal(new[] { "report-a", "report-b" }, groups.Select(group => group.SubmissionId));
        Assert.All(groups, group => Assert.Single(group.Photos));
    }

    [Fact]
    public void MatchesReportIdsExactlyWithoutCaseFoldingOrTrimming()
    {
        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> groups = ReportedPhotoGrouper.Group(new[]
        {
            Photo("lowercase", "report"),
            Photo("uppercase", "Report"),
            Photo("padded", " report ")
        });

        Assert.Equal(new[] { " report ", "Report", "report" }, groups.Select(group => group.SubmissionId));
        Assert.All(groups, group => Assert.Single(group.Photos));
    }

    [Fact]
    public void SameReportGroupsPhotosCapturedAtDifferentTimes()
    {
        TestPhoto older = Photo("older", "report") with { CreatedAt = Now.AddDays(-7) };
        TestPhoto newer = Photo("newer", "report");

        ReportedPhotoGroup<TestPhoto> group = Assert.Single(ReportedPhotoGrouper.Group(new[] { older, newer }));

        Assert.Equal("report", group.SubmissionId);
        Assert.Equal(new[] { "newer", "older" }, group.Photos.Select(photo => photo.Id));
    }

    [Fact]
    public void OrdersSubmissionAndCaptureTimesChronologicallyAcrossOffsets()
    {
        DateTimeOffset earlier = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.FromHours(2));
        DateTimeOffset later = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.FromHours(-7));
        TestPhoto olderReport = Photo("older-report-photo", "older") with { SubmittedAt = earlier };
        TestPhoto earlierCapture = Photo("earlier-capture", "newer") with
        {
            SubmittedAt = later,
            CreatedAt = earlier
        };
        TestPhoto laterCapture = Photo("later-capture", "newer") with
        {
            SubmittedAt = later.ToOffset(TimeSpan.FromHours(3)),
            CreatedAt = later
        };

        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> groups =
            ReportedPhotoGrouper.Group(new[] { olderReport, earlierCapture, laterCapture });

        Assert.Equal(new[] { "newer", "older" }, groups.Select(group => group.SubmissionId));
        Assert.Equal(new[] { "later-capture", "earlier-capture" }, groups[0].Photos.Select(photo => photo.Id));
        Assert.Equal(later, groups[0].SubmittedAt);
    }

    [Fact]
    public void UsesMaximumKnownSubmissionTimeAcrossAllMembers()
    {
        DateTimeOffset earlier = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.FromHours(2));
        DateTimeOffset latest = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.FromHours(-7));
        TestPhoto[] photos =
        {
            Photo("recent-capture", "report") with { SubmittedAt = earlier },
            Photo("old-capture", "report") with { SubmittedAt = latest, CreatedAt = Now.AddDays(-2) },
            Photo("missing-time", "report") with { SubmittedAt = null },
            Photo("other", "other-report") with { SubmittedAt = latest.AddMinutes(-1) }
        };

        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> groups = ReportedPhotoGrouper.Group(photos);

        Assert.Equal(new[] { "report", "other-report" }, groups.Select(group => group.SubmissionId));
        Assert.Equal(latest, groups[0].SubmittedAt);
        Assert.Equal(3, groups[0].Photos.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BreaksGroupTimeTiesByOrdinalReportIdThenUnknownPhotoId(bool reverse)
    {
        TestPhoto[] photos =
        {
            Photo("unknown-z", null),
            Photo("lowercase", "a"),
            Photo("unknown-A", ""),
            Photo("uppercase", "A"),
            Photo("unknown-a", " ")
        };

        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> groups =
            ReportedPhotoGrouper.Group(reverse ? photos.Reverse() : photos);

        Assert.Equal(new string?[] { "A", "a", null, null, null }, groups.Select(group => group.SubmissionId));
        Assert.Equal(new[] { "uppercase", "lowercase", "unknown-A", "unknown-a", "unknown-z" },
            groups.Select(group => Assert.Single(group.Photos).Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdersPhotosNewestFirstThenOrdinalIdWithMissingCaptureTimesLast(bool reverse)
    {
        TestPhoto[] photos =
        {
            Photo("undated-z", "report") with { CreatedAt = null },
            Photo("a", "report"),
            Photo("older", "report") with { CreatedAt = Now.AddHours(-1) },
            Photo("newest", "report") with { CreatedAt = Now },
            Photo("A", "report") with { CreatedAt = Now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(2)) },
            Photo("undated-A", "report") with { CreatedAt = null }
        };

        ReportedPhotoGroup<TestPhoto> group = Assert.Single(
            ReportedPhotoGrouper.Group(reverse ? photos.Reverse() : photos));

        Assert.Equal(new[] { "newest", "A", "a", "older", "undated-A", "undated-z" },
            group.Photos.Select(photo => photo.Id));
    }

    [Fact]
    public void MissingIdsStaySeparateAndCannotCollideWithRealReportIdsOrPhotoIds()
    {
        TestPhoto[] unknown =
        {
            Photo("shared-id", null),
            Photo("shared-id", ""),
            Photo("space", " "),
            Photo("whitespace", "\t\r\n")
        };
        TestPhoto known = Photo("known", "shared-id");

        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> groups =
            ReportedPhotoGrouper.Group(unknown.Append(known));

        Assert.Equal(5, groups.Count);
        ReportedPhotoGroup<TestPhoto> knownGroup = Assert.Single(groups, group => group.SubmissionId is not null);
        Assert.Equal("shared-id", knownGroup.SubmissionId);
        Assert.Same(known, Assert.Single(knownGroup.Photos));
        foreach (TestPhoto photo in unknown)
        {
            ReportedPhotoGroup<TestPhoto> group = Assert.Single(groups,
                group => ReferenceEquals(group.Photos[0], photo));
            Assert.Null(group.SubmissionId);
            Assert.Same(photo, Assert.Single(group.Photos));
        }
    }

    [Fact]
    public void MissingTimesAreNotInventedAndUnknownTimeGroupsSortLast()
    {
        TestPhoto[] photos =
        {
            Photo("known-undated-a", "a-report") with { SubmittedAt = null, CreatedAt = Now.AddYears(1) },
            Photo("known-undated-b", "a-report") with { SubmittedAt = null, CreatedAt = null },
            Photo("unknown-undated", null) with { SubmittedAt = null },
            Photo("dated", "z-report") with { SubmittedAt = Now.AddYears(-1) },
            Photo("unknown-dated", null)
        };

        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> groups = ReportedPhotoGrouper.Group(photos);

        Assert.Equal(4, groups.Count);
        Assert.Null(groups[0].SubmissionId);
        Assert.Equal("unknown-dated", Assert.Single(groups[0].Photos).Id);
        Assert.Equal(Now, groups[0].SubmittedAt);
        Assert.Equal("z-report", groups[1].SubmissionId);
        Assert.Equal(Now.AddYears(-1), groups[1].SubmittedAt);
        Assert.Equal("a-report", groups[2].SubmissionId);
        Assert.Null(groups[2].SubmittedAt);
        Assert.Equal(2, groups[2].Photos.Count);
        Assert.Null(groups[3].SubmissionId);
        Assert.Null(groups[3].SubmittedAt);
        Assert.Equal("unknown-undated", Assert.Single(groups[3].Photos).Id);
        Assert.Equal(photos.Length, groups.Sum(group => group.Photos.Count));
    }

    [Fact]
    public void RebuildingAfterRemovalRecalculatesMembersTimesAndGroupOrder()
    {
        TestPhoto earliest = Photo("earliest", "first-report") with { SubmittedAt = Now.AddHours(-2) };
        TestPhoto latest = Photo("latest", "first-report");
        TestPhoto other = Photo("other", "second-report") with { SubmittedAt = Now.AddHours(-1) };
        List<TestPhoto> photos = new List<TestPhoto> { earliest, latest, other };

        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> original = ReportedPhotoGrouper.Group(photos);
        Assert.Equal(new[] { "first-report", "second-report" }, original.Select(group => group.SubmissionId));

        photos.Remove(latest);
        IReadOnlyList<ReportedPhotoGroup<TestPhoto>> afterMemberRemoval = ReportedPhotoGrouper.Group(photos);
        Assert.Equal(new[] { "second-report", "first-report" },
            afterMemberRemoval.Select(group => group.SubmissionId));
        Assert.Equal(earliest.SubmittedAt, afterMemberRemoval[1].SubmittedAt);
        Assert.Same(earliest, Assert.Single(afterMemberRemoval[1].Photos));
        Assert.Equal(2, original[0].Photos.Count);
        Assert.Equal(Now, original[0].SubmittedAt);

        photos.Remove(earliest);
        ReportedPhotoGroup<TestPhoto> remaining = Assert.Single(ReportedPhotoGrouper.Group(photos));
        Assert.Equal("second-report", remaining.SubmissionId);
        Assert.Same(other, Assert.Single(remaining.Photos));

        photos.Clear();
        Assert.Empty(ReportedPhotoGrouper.Group(photos));
    }
}
