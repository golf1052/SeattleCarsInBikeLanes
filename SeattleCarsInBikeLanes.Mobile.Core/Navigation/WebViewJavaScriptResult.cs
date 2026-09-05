using System.Text.Json;

namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

/// <summary>
/// Normalizes JSON returned by a MAUI WebView's JavaScript evaluator.
/// </summary>
public static class WebViewJavaScriptResult
{
    /// <summary>
    /// Removes the platform-specific string encoding around a JSON value.
    /// </summary>
    /// <remarks>
    /// iOS returns a JSON string including its outer quotes. Android returns the contents of that
    /// string with its quotes still escaped, but without the outer quotes.
    /// </remarks>
    public static string DecodeJson(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string trimmed = value.Trim();
        if (trimmed.StartsWith('"'))
        {
            return JsonSerializer.Deserialize<string>(trimmed) ?? trimmed;
        }

        if (trimmed.Length > 2 &&
            (trimmed[0] == '{' || trimmed[0] == '[') &&
            trimmed[1] == '\\')
        {
            return JsonSerializer.Deserialize<string>($"\"{trimmed}\"") ?? trimmed;
        }

        return trimmed;
    }
}
