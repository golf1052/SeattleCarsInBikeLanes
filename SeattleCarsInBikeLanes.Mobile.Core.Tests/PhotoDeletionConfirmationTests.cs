using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class PhotoDeletionConfirmationTests
{
    [Fact]
    public void IosCapturedOnlyUsesSystemConfirmation()
    {
        Assert.Null(PhotoDeletionConfirmation.Build(
            capturedCount: 2,
            importedCount: 0,
            platformConfirmsCapturedDeletion: true));
    }

    [Fact]
    public void AndroidCapturedOnlyUsesAppConfirmation()
    {
        Assert.Equal(
            "2 photos taken in the app will be deleted from your library.",
            PhotoDeletionConfirmation.Build(
                capturedCount: 2,
                importedCount: 0,
                platformConfirmsCapturedDeletion: false));
    }

    [Fact]
    public void ImportedPhotoExplainsThatOriginalIsKept()
    {
        Assert.Equal(
            "1 imported photo will be removed from Cars in Bike Lanes but kept in your library.",
            PhotoDeletionConfirmation.Build(
                capturedCount: 0,
                importedCount: 1,
                platformConfirmsCapturedDeletion: false));
    }

    [Fact]
    public void MixedSelectionDescribesBothOutcomes()
    {
        Assert.Equal(
            "1 photo taken in the app will be deleted from your library. " +
            "2 imported photos will be removed from Cars in Bike Lanes but kept in your library.",
            PhotoDeletionConfirmation.Build(
                capturedCount: 1,
                importedCount: 2,
                platformConfirmsCapturedDeletion: true));
    }

    [Fact]
    public void PrivateCapturedPhotoUsesAppConfirmation()
    {
        Assert.Equal(
            "1 photo kept privately in the app will be deleted.",
            PhotoDeletionConfirmation.Build(
                capturedCount: 0,
                importedCount: 0,
                platformConfirmsCapturedDeletion: true,
                privateCapturedCount: 1));
    }
}
