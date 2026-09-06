using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class PhotoRenditionPolicyTests
{
    private sealed record Resource(string Kind, string State);

    [Fact]
    public void RecreatedReaderUsesStampedCurrentResourceNotOriginal()
    {
        Resource[] resources = [new("original", "unreported"), new("current", "receipt-1")];
        Assert.Equal("receipt-1", Read(resources));
        Assert.Equal("receipt-1", Read(resources.Select(r => r with { }).ToArray()));
    }

    [Fact]
    public void OriginalIsUsedOnlyWithoutCurrentAndOtherResourcesAreIgnored()
    {
        Assert.Equal("unreported", Read([new("adjustment", "wrong"), new("original", "unreported")]));
        Assert.Null(Read([new("adjustment", "wrong")]));
        Assert.Equal("unavailable", Read([new("original", "unreported"), new("current", "unavailable")]));
    }

    [Fact]
    public void OwnAndForeignAdjustmentsRequireRenderedContent() =>
        Assert.False(PhotoRenditionPolicy.CanReconstructAdjustments);

    private static string? Read(Resource[] resources) =>
        PhotoRenditionPolicy.SelectCurrent(resources, r => r.Kind == "current", r => r.Kind == "original")?.State;
}
