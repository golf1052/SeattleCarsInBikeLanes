using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

/// <summary>
/// Folding the server's reading of a photo into a queued report.
/// </summary>
/// <remarks>
/// This replaced a round trip through the report page, where the server's answers came back mid
/// submit and the user was asked to react to them. With a queue there is nobody there to react, so
/// the merge has to be safe on its own: it may only ever improve the report, never leave it less
/// sendable than it arrived.
/// </remarks>
public class ReportDraftMergeTests
{
    private static readonly BoundingBox Seattle = BoundingBox.Seattle;

    private static readonly DateTime Now = new DateTime(2025, 6, 1, 12, 0, 0);

    private static ReportDraft Draft() => new ReportDraft()
    {
        NumberOfCars = 1,
        TakenAt = new DateTime(2025, 6, 1, 9, 0, 0),
        Location = new GeoPosition(47.6062, -122.3321)
    };

    [Fact]
    public void TakesTheServersReadingOfThePhoto()
    {
        DateTime serverDate = new DateTime(2025, 6, 1, 8, 45, 0);
        GeoPosition serverLocation = new GeoPosition(47.62, -122.35);

        ReportDraft merged = ReportDraftMerge.WithServerValues(Draft(),
            serverDate,
            serverLocation,
            "Pine St",
            Seattle,
            Now);

        Assert.Equal(serverDate, merged.TakenAt);
        Assert.Equal(serverLocation, merged.Location);
        Assert.Equal("Pine St", merged.CrossStreet);
    }

    [Fact]
    public void LeavesWhatTheUserTypedAlone()
    {
        ReportDraft draft = Draft();
        draft.UserSpecifiedDateTime = true;
        draft.UserSpecifiedLocation = true;

        ReportDraft merged = ReportDraftMerge.WithServerValues(draft,
            new DateTime(2025, 6, 1, 8, 45, 0),
            new GeoPosition(47.62, -122.35),
            "Pine St",
            Seattle,
            Now);

        // The user was looking at the thing being reported. The camera's clock was not.
        Assert.Equal(draft.TakenAt, merged.TakenAt);
        Assert.Equal(draft.Location, merged.Location);

        // A cross street belongs to the position it was worked out from, so one the user moved the
        // pin away from must not be carried over.
        Assert.Null(merged.CrossStreet);
    }

    [Fact]
    public void IgnoresADateFromTheFuture()
    {
        // A camera with a wrong clock would otherwise produce a report the server refuses at
        // finalize, with nobody there to fix it.
        ReportDraft draft = Draft();

        ReportDraft merged = ReportDraftMerge.WithServerValues(draft,
            Now.AddDays(1),
            null,
            null,
            Seattle,
            Now);

        Assert.Equal(draft.TakenAt, merged.TakenAt);
    }

    [Fact]
    public void IgnoresALocationOutsideTheBox()
    {
        ReportDraft draft = Draft();

        ReportDraft merged = ReportDraftMerge.WithServerValues(draft,
            null,
            new GeoPosition(40.7128, -74.0060),
            null,
            Seattle,
            Now);

        Assert.Equal(draft.Location, merged.Location);
    }

    [Fact]
    public void KeepsTheReportSendableWhenTheServerSaysNothing()
    {
        ReportDraft draft = Draft();

        ReportDraft merged = ReportDraftMerge.WithServerValues(draft, null, null, null, Seattle, Now);

        Assert.Equal(draft.TakenAt, merged.TakenAt);
        Assert.Equal(draft.Location, merged.Location);

        ValidationResult validation = ReportValidator.Validate(merged, 1, Seattle, 4, Now);
        Assert.True(validation.IsValid);
    }

    [Fact]
    public void LeavesTheQueuedReportUntouched()
    {
        // A second attempt has to start from what the user filled in, not from whatever the failed
        // attempt left behind.
        ReportDraft draft = Draft();
        DateTime original = draft.TakenAt!.Value;

        ReportDraftMerge.WithServerValues(draft,
            new DateTime(2025, 6, 1, 8, 45, 0),
            new GeoPosition(47.62, -122.35),
            "Pine St",
            Seattle,
            Now);

        Assert.Equal(original, draft.TakenAt);
        Assert.Equal(new GeoPosition(47.6062, -122.3321), draft.Location);
        Assert.Null(draft.CrossStreet);
    }
}
