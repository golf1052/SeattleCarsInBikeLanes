namespace SeattleCarsInBikeLanes.Mobile.Core.Authentication;

public static class CookieHeaderParser
{
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        List<KeyValuePair<string, string>> cookies = new List<KeyValuePair<string, string>>();
        int start = 0;
        bool quoted = false;
        for (int index = 0; index <= header.Length; index++)
        {
            if (index < header.Length && header[index] == '"')
            {
                quoted = !quoted;
            }

            if (index < header.Length && (header[index] != ';' || quoted))
            {
                continue;
            }

            ReadOnlySpan<char> pair = header.AsSpan(start, index - start).Trim();
            int equals = pair.IndexOf('=');
            if (equals > 0)
            {
                string name = pair[..equals].Trim().ToString();
                string value = pair[(equals + 1)..].Trim().ToString();
                if (name.Length > 0)
                {
                    cookies.Add(new KeyValuePair<string, string>(name, value));
                }
            }

            start = index + 1;
        }

        return cookies;
    }
}
