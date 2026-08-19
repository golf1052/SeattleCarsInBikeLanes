using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class FinalizeRequestBuilderTests
{
    private static List<InitialPhotoUpload> Photos(int count = 1)
    {
        List<InitialPhotoUpload> photos = new List<InitialPhotoUpload>();
        for (int i = 0; i < count; i++)
        {
            photos.Add(new InitialPhotoUpload()
            {
                PhotoId = $"photo-{i}",
                SubmissionId = "submission-1",
                PhotoNumber = i,
                PhotoDateTime = new DateTime(2026, 4, 1, 9, 0, 0),
                PhotoLatitude = "47.60621",
                PhotoLongitude = "-122.33207",
                PhotoCrossStreet = "Pike St"
            });
        }

        return photos;
    }

    private static ReportDraft Draft() => new ReportDraft()
    {
        NumberOfCars = 2,
        TakenAt = new DateTime(2026, 4, 1, 9, 0, 0)
    };

    [Fact]
    public void AppliesTheReportToEveryPhoto()
    {
        List<FinalizedPhotoUpload> result = FinalizeRequestBuilder.Build(Photos(3), Draft(), null);

        Assert.Equal(3, result.Count);
        Assert.All(result, photo => Assert.Equal(2, photo.NumberOfCars));
        Assert.All(result, photo => Assert.Equal("submission-1", photo.SubmissionId));
        Assert.Equal(new[] { 0, 1, 2 }, result.Select(photo => photo.PhotoNumber));
    }

    [Fact]
    public void KeepsTheServersLocationWhenTheUserDidNotChangeIt()
    {
        List<FinalizedPhotoUpload> result = FinalizeRequestBuilder.Build(Photos(), Draft(), null);

        Assert.Equal("47.60621", result[0].PhotoLatitude);
        Assert.Equal("Pike St", result[0].PhotoCrossStreet);
    }

    [Fact]
    public void ClearsTheCrossStreetWhenTheUserMovedThePin()
    {
        ReportDraft draft = Draft();
        draft.Location = new GeoPosition(47.65, -122.35);
        draft.UserSpecifiedLocation = true;

        List<FinalizedPhotoUpload> result = FinalizeRequestBuilder.Build(Photos(), draft, null);

        Assert.Equal("47.65", result[0].PhotoLatitude);
        Assert.Equal("-122.35", result[0].PhotoLongitude);

        // A blank cross street is how the server is told to reverse geocode the new position.
        Assert.Null(result[0].PhotoCrossStreet);
        Assert.True(result[0].UserSpecifiedLocation);
    }

    [Fact]
    public void DoesNotAttributeWhenTheUserDidNotAskForIt()
    {
        AttributionIdentity identity = new AttributionIdentity() { BlueskyHandle = "someone.bsky.social" };

        List<FinalizedPhotoUpload> result = FinalizeRequestBuilder.Build(Photos(), Draft(), identity);

        Assert.Null(result[0].Attribute);
        Assert.Equal("Submission", result[0].BlueskySubmittedBy);
    }

    [Fact]
    public void DoesNotAttributeWhenNobodyIsSignedIn()
    {
        ReportDraft draft = Draft();
        draft.Attribute = true;

        List<FinalizedPhotoUpload> result = FinalizeRequestBuilder.Build(Photos(), draft, null);

        Assert.Null(result[0].Attribute);
        Assert.Equal("Submission", result[0].BlueskySubmittedBy);
        Assert.Equal("Submission", result[0].MastodonSubmittedBy);
        Assert.Equal("Submission", result[0].TwitterSubmittedBy);
    }

    [Fact]
    public void CreditsASignedInBlueskyUser()
    {
        ReportDraft draft = Draft();
        draft.Attribute = true;
        AttributionIdentity identity = new AttributionIdentity() { BlueskyHandle = "someone.bsky.social" };

        List<FinalizedPhotoUpload> result = FinalizeRequestBuilder.Build(Photos(2), draft, identity);

        Assert.All(result, photo => Assert.True(photo.Attribute));
        Assert.All(result, photo => Assert.Equal("Submitted by someone.bsky.social", photo.BlueskySubmittedBy));
    }

    [Fact]
    public void SendsMastodonCredentialsOnlyWhenCrediting()
    {
        AttributionIdentity identity = new AttributionIdentity()
        {
            MastodonUsername = "someone",
            MastodonFullUsername = "@someone@example.social",
            MastodonEndpoint = "https://example.social",
            MastodonAccessToken = "secret-token"
        };

        List<FinalizedPhotoUpload> notCredited = FinalizeRequestBuilder.Build(Photos(), Draft(), identity);
        Assert.Null(notCredited[0].MastodonAccessToken);

        ReportDraft draft = Draft();
        draft.Attribute = true;
        List<FinalizedPhotoUpload> credited = FinalizeRequestBuilder.Build(Photos(), draft, identity);

        Assert.Equal("secret-token", credited[0].MastodonAccessToken);
        Assert.Equal("Submitted by @someone@example.social", credited[0].MastodonSubmittedBy);
    }

    [Fact]
    public void AnIdentityWithoutAUsableAccountCannotCredit()
    {
        ReportDraft draft = Draft();
        draft.Attribute = true;

        // A Mastodon token with no endpoint is not enough for the server to verify anyone.
        AttributionIdentity identity = new AttributionIdentity() { MastodonAccessToken = "secret-token" };

        List<FinalizedPhotoUpload> result = FinalizeRequestBuilder.Build(Photos(), draft, identity);

        Assert.Null(result[0].Attribute);
        Assert.Null(result[0].MastodonAccessToken);
    }

    [Fact]
    public void RequiresAtLeastOnePhoto()
    {
        Assert.Throws<ArgumentException>(() =>
            FinalizeRequestBuilder.Build(new List<InitialPhotoUpload>(), Draft(), null));
    }
}
