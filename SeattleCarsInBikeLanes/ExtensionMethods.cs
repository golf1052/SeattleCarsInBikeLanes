using System.Runtime.InteropServices;

namespace SeattleCarsInBikeLanes
{
    public static class ExtensionMethods
    {
        public static DateTime ConvertLocalTimeOnlyToUtcDateTime(this TimeOnly time, DateOnly date)
        {
            TimeZoneInfo timeZoneInfo;
            DateTimeOffset dateTimeOffset;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            }
            else
            {
                timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
            }

            DateTime dateTime = date.ToDateTime(time, DateTimeKind.Unspecified);
            dateTimeOffset = TimeZoneInfo.ConvertTimeToUtc(dateTime, timeZoneInfo);
            return dateTimeOffset.UtcDateTime;
        }
    }
}
