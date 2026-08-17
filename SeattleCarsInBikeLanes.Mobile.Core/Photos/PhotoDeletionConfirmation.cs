namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

public static class PhotoDeletionConfirmation
{
    public static string? Build(
        int capturedCount,
        int importedCount,
        bool platformConfirmsCapturedDeletion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capturedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(importedCount);

        if (importedCount == 0 &&
            (capturedCount == 0 || platformConfirmsCapturedDeletion))
        {
            return null;
        }

        string? importedPart = importedCount switch
        {
            0 => null,
            1 => "1 imported photo will be removed from Cars in Bike Lanes but kept in your library.",
            _ => $"{importedCount} imported photos will be removed from Cars in Bike Lanes but kept in your library."
        };

        if (capturedCount == 0)
        {
            return importedPart;
        }

        string capturedPart = capturedCount == 1
            ? "1 photo taken in the app will be deleted from your library."
            : $"{capturedCount} photos taken in the app will be deleted from your library.";

        return importedPart is null ? capturedPart : $"{capturedPart} {importedPart}";
    }
}
