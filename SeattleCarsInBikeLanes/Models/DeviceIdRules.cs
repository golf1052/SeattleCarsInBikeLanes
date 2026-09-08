namespace SeattleCarsInBikeLanes.Models
{
    public static class DeviceIdRules
    {
        public static string? Normalize(string? deviceId)
        {
            return string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
        }

        public static bool IsValid(string? normalizedId)
        {
            return normalizedId is { Length: >= 1 and <= 128 } &&
                normalizedId.All(character => character is >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
        }
    }
}
