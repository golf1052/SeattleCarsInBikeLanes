using System.Text.Json;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;
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
                new QueuedPhoto() { Id = "captured", Origin = PhotoOrigin.Captured },
                new QueuedPhoto() { Id = "imported", Origin = PhotoOrigin.Imported },
                new QueuedPhoto() { Id = "private-captured", Origin = PhotoOrigin.PrivateCaptured },
                new QueuedPhoto() { Id = "private-imported", Origin = PhotoOrigin.PrivateImported }
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
        Assert.Collection(read.Photos,
            photo =>
            {
                Assert.Equal("captured", photo.Id);
                Assert.Equal(PhotoOrigin.Captured, photo.Origin);
            },
            photo =>
            {
                Assert.Equal("imported", photo.Id);
                Assert.Equal(PhotoOrigin.Imported, photo.Origin);
            },
            photo =>
            {
                Assert.Equal("private-captured", photo.Id);
                Assert.Equal(PhotoOrigin.PrivateCaptured, photo.Origin);
            },
            photo =>
            {
                Assert.Equal("private-imported", photo.Id);
                Assert.Equal(PhotoOrigin.PrivateImported, photo.Origin);
            });

        Assert.Equal(3, read.Draft.NumberOfCars);
        Assert.Equal(new DateTime(2025, 6, 1, 9, 30, 0), read.Draft.TakenAt);
        Assert.Equal(new GeoPosition(47.6062, -122.3321), read.Draft.Location);
        Assert.True(read.Draft.UserSpecifiedDateTime);
        Assert.True(read.Draft.UserSpecifiedLocation);
        Assert.True(read.Draft.Attribute);
        Assert.Equal("Pine St", read.Draft.CrossStreet);
    }

    [Fact]
    public void WritesStableReadablePhotoOrigins()
    {
        QueuedReportPayload payload = new QueuedReportPayload()
        {
            Photos = new List<QueuedPhoto>()
            {
                new QueuedPhoto() { Id = "captured", Origin = PhotoOrigin.Captured },
                new QueuedPhoto() { Id = "imported", Origin = PhotoOrigin.Imported },
                new QueuedPhoto() { Id = "private-captured", Origin = PhotoOrigin.PrivateCaptured },
                new QueuedPhoto() { Id = "private-imported", Origin = PhotoOrigin.PrivateImported }
            }
        };

        string json = QueuedReportSerializer.Serialize(payload);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement photos = document.RootElement.GetProperty("photos");

        Assert.Equal("captured", photos[0].GetProperty("origin").GetString());
        Assert.Equal("imported", photos[1].GetProperty("origin").GetString());
        Assert.Equal("privateCaptured", photos[2].GetProperty("origin").GetString());
        Assert.Equal("privateImported", photos[3].GetProperty("origin").GetString());
        Assert.DoesNotContain("\"imported\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"private\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotWriteTheAccessToken()
    {
        // The finalize body carries the user's Mastodon token, and a queue file outlives the
        // process. Only the wish to be credited is kept; the credential is read back from secure
        // storage when the report is actually sent.
        QueuedReportPayload payload = new QueuedReportPayload()
        {
            Photos = new List<QueuedPhoto>()
            {
                new QueuedPhoto() { Id = "asset-1", Origin = PhotoOrigin.Captured }
            },
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
    [InlineData("{\"photos\":null,\"draft\":{}}")]
    [InlineData("{\"photos\":[null],\"draft\":{}}")]
    [InlineData("{\"photos\":[{\"id\":\"asset-1\",\"origin\":\"captured\"}],\"draft\":null}")]
    [InlineData("{\"photos\":[{\"id\":\"asset-1\"}],\"draft\":{}}")]
    [InlineData("{\"photos\":[{\"id\":\"asset-1\",\"imported\":false,\"private\":false}],\"draft\":{}}")]
    [InlineData("{\"photos\":[{\"id\":\"asset-1\",\"origin\":\"unknown\"}],\"draft\":{}}")]
    [InlineData("{\"photos\":[{\"id\":\"asset-1\",\"origin\":0}],\"draft\":{}}")]
    public void UnreadableRowsAreRejectedRatherThanThrown(string? json)
    {
        // The caller drains queue rows on a background thread, so one malformed row must not stop
        // every other report from going out.
        Assert.Null(QueuedReportSerializer.Deserialize(json));
    }
}
