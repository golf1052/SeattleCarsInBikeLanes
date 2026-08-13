using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

/// <summary>
/// What a queued report has to survive being written to disk and read back.
/// </summary>
/// <remarks>
/// A report can sit in the queue across a suspension, a crash, or an app update, so anything lost
/// in the round trip is lost from the report the user believes they submitted.
/// </remarks>
public class QueuedReportSerializerTests
{
    [Fact]
    public void RoundTripsAReport()
    {
        QueuedReportPayload payload = new QueuedReportPayload()
        {
            Photos = new List<QueuedPhoto>()
            {
                new QueuedPhoto() { Id = "asset-1", Imported = false },
                new QueuedPhoto() { Id = "asset-2", Imported = true }
            },
            Draft = new ReportDraft()
            {
                NumberOfCars = 3,
                TakenAt = new DateTime(2025, 6, 1, 9, 30, 0),
                Location = new GeoPosition(47.6062, -122.3321),
                UserSpecifiedDateTime = true,
                UserSpecifiedLocation = true,
                Attribute = true,
                CrossStreet = "Pine St"
            }
        };

        QueuedReportPayload? read = QueuedReportSerializer.Deserialize(QueuedReportSerializer.Serialize(payload));

        Assert.NotNull(read);
        Assert.Equal(2, read.Photos.Count);
        Assert.Equal("asset-1", read.Photos[0].Id);
        Assert.False(read.Photos[0].Imported);

        // Which store a photo's submitted flag goes in depends on this, and by the time a queued
        // report succeeds the roll it came from is long gone.
        Assert.True(read.Photos[1].Imported);

        Assert.Equal(3, read.Draft.NumberOfCars);
        Assert.Equal(new DateTime(2025, 6, 1, 9, 30, 0), read.Draft.TakenAt);
        Assert.Equal(new GeoPosition(47.6062, -122.3321), read.Draft.Location);
        Assert.True(read.Draft.UserSpecifiedDateTime);
        Assert.True(read.Draft.UserSpecifiedLocation);
        Assert.True(read.Draft.Attribute);
        Assert.Equal("Pine St", read.Draft.CrossStreet);
    }

    [Fact]
    public void DoesNotWriteTheAccessToken()
    {
        // The finalize body carries the user's Mastodon token, and a queue file outlives the
        // process. Only the wish to be credited is kept; the credential is read back from secure
        // storage when the report is actually sent.
        QueuedReportPayload payload = new QueuedReportPayload()
        {
            Photos = new List<QueuedPhoto>() { new QueuedPhoto() { Id = "asset-1" } },
            Draft = new ReportDraft() { Attribute = true }
        };

        string json = QueuedReportSerializer.Serialize(payload);

        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mastodon", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"photos\":[]}")]
    public void UnreadableRowsAreRejectedRatherThanThrown(string? json)
    {
        // The caller is a queue drained on a background thread. A row written by an older build must
        // not be able to stop every other report from going out.
        Assert.Null(QueuedReportSerializer.Deserialize(json));
    }
}
