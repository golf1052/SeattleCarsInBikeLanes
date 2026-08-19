using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.Controllers;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Tests;

public class UploadContractTests
{
    [Fact]
    public void InitialMetadataMapsToTheSharedResponse()
    {
        DateTime takenAt = new DateTime(2026, 8, 18, 12, 30, 0);
        List<ImageTag> tags = new List<ImageTag>()
        {
            new ImageTag() { Name = "car", Confidence = 0.95f }
        };
        InitialPhotoUploadMetadata metadata = new InitialPhotoUploadMetadata(
            "photo-1",
            "submission-1",
            0,
            takenAt,
            "47.60621",
            "-122.33207",
            "Pike St",
            tags);

        InitialPhotoUpload contract = metadata.ToContract("https://example.test/photo");

        Assert.Equal("https://example.test/photo", contract.Uri);
        Assert.Equal(metadata.PhotoId, contract.PhotoId);
        Assert.Equal(metadata.SubmissionId, contract.SubmissionId);
        Assert.Equal(takenAt, contract.PhotoDateTime);
        Assert.Same(tags, contract.Tags);
    }

    [Fact]
    public void FinalizeContractMapsOnlyClientWritableFields()
    {
        FinalizedPhotoUpload contract = new FinalizedPhotoUpload()
        {
            PhotoId = "photo-1",
            SubmissionId = "submission-1",
            PhotoNumber = 0,
            NumberOfCars = 2,
            TwitterAccessToken = "twitter-token",
            MastodonAccessToken = "mastodon-token",
            ThreadsAccessToken = "threads-token",
            BlueskySubmittedBy = "Submitted by rider.bsky.social"
        };

        FinalizedPhotoUploadMetadata metadata =
            FinalizedPhotoUploadMetadata.FromContract(contract);

        Assert.Equal(contract.PhotoId, metadata.PhotoId);
        Assert.Equal(contract.NumberOfCars, metadata.NumberOfCars);
        Assert.Equal("twitter-token", metadata.TwitterAccessToken);
        Assert.Equal("mastodon-token", metadata.MastodonAccessToken);
        Assert.Equal("threads-token", metadata.ThreadsAccessToken);
        Assert.Equal(contract.BlueskySubmittedBy, metadata.BlueskySubmittedBy);
        Assert.Null(metadata.DeviceId);
        Assert.Null(metadata.ReportId);
        Assert.Null(metadata.BlueskyHandle);
        Assert.Null(metadata.BlueskyUserDid);
    }

    [Fact]
    public void LimitsEndpointReturnsTheSharedContract()
    {
        UploadController controller = new UploadController(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        OkObjectResult result = Assert.IsType<OkObjectResult>(controller.GetLimits());
        UploadLimits limits = Assert.IsType<UploadLimits>(result.Value);

        Assert.Equal(UploadController.MaxPhotosPerReport, limits.MaxPhotosPerReport);
        Assert.Equal(47.495082, limits.SouthLatitude);
        Assert.Equal(-122.436522, limits.WestLongitude);
        Assert.Equal(47.735525, limits.NorthLatitude);
        Assert.Equal(-122.235787, limits.EastLongitude);
    }
}
