namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

public static class PhotoDeletionConfirmation
{
    public static string? Build(
        int capturedCount,
        int importedCount,
        bool platformConfirmsCapturedDeletion,
        int privateCapturedCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capturedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(importedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(privateCapturedCount);

        if (importedCount == 0 && privateCapturedCount == 0 &&
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

        string? privatePart = privateCapturedCount switch
        {
            0 => null,
            1 => "1 photo kept privately in the app will be deleted.",
            _ => $"{privateCapturedCount} photos kept privately in the app will be deleted."
        };

        if (capturedCount == 0 && privatePart is null)
        {
            return importedPart;
        }

        string? capturedPart = capturedCount switch
        {
            0 => null,
            1 => "1 photo taken in the app will be deleted from your library.",
            _ => $"{capturedCount} photos taken in the app will be deleted from your library."
        };

        return string.Join(" ", new[] { capturedPart, privatePart, importedPart }
            .Where(part => part is not null));
    }
}
