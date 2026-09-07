namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

public static class PhotoRenditionPolicy
{
    // A metadata-only editor cannot reconstruct either foreign or its own earlier adjustments.
    public static bool CanReconstructAdjustments => false;

    public static T? SelectCurrent<T>(IReadOnlyList<T> resources,
        Func<T, bool> isCurrent, Func<T, bool> isOriginal) where T : class =>
        resources.FirstOrDefault(isCurrent) ?? resources.FirstOrDefault(isOriginal);
}
