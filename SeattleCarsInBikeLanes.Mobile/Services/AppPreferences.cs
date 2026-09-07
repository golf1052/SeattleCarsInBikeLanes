namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// The app's user preferences.
/// </summary>
/// <remarks>
/// Kept in one place because these are read in one part of the app and written in another, and a
/// mistyped key would silently read back the default rather than failing.
/// </remarks>
public static class AppPreferences
{
    private const string AttributeByDefaultKey = "AttributeByDefault";
    private const string CameraPermissionRequestedKey = "CameraPermissionRequested";
    private const string PhotoLibraryPermissionRequestedKey = "PhotoLibraryPermissionRequested";
    private const string LocationPermissionRequestedKey = "LocationPermissionRequested";

    /// <summary>
    /// Whether reports are credited to the signed in user unless they say otherwise.
    /// </summary>
    /// <remarks>
    /// Defaults to on. Somebody who has gone to the trouble of signing in has already said they
    /// want to be credited.
    /// </remarks>
    public static bool AttributeByDefault
    {
        get => Preferences.Default.Get(AttributeByDefaultKey, true);
        set => Preferences.Default.Set(AttributeByDefaultKey, value);
    }

    /// <summary>
    /// Whether the app has already presented its one automatic camera permission request.
    /// </summary>
    public static bool CameraPermissionRequested
    {
        get => Preferences.Default.Get(CameraPermissionRequestedKey, false);
        set => Preferences.Default.Set(CameraPermissionRequestedKey, value);
    }

    /// <summary>
    /// Whether the app has already presented its one automatic photo-library permission request.
    /// </summary>
    public static bool PhotoLibraryPermissionRequested
    {
        get => Preferences.Default.Get(PhotoLibraryPermissionRequestedKey, false);
        set => Preferences.Default.Set(PhotoLibraryPermissionRequestedKey, value);
    }

    /// <summary>
    /// Whether the app has already presented its one automatic location permission request.
    /// </summary>
    public static bool LocationPermissionRequested
    {
        get => Preferences.Default.Get(LocationPermissionRequestedKey, false);
        set => Preferences.Default.Set(LocationPermissionRequestedKey, value);
    }
}
