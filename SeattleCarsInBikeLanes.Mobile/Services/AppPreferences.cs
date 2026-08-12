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
}
