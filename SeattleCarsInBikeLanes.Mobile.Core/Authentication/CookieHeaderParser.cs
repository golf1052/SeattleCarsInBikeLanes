using Microsoft.Net.Http.Headers;

namespace SeattleCarsInBikeLanes.Mobile.Core.Authentication;

/// <summary>
/// Parses Android WebView request-cookie headers using the standard RFC 6265 parser.
/// </summary>
public static class CookieHeaderParser
{
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        return CookieHeaderValue.ParseList([header])
            .Select(cookie => KeyValuePair.Create(
                cookie.Name.ToString(),
                cookie.Value.ToString()))
            .ToArray();
    }
}
