namespace Orleans.AdvancedReminders.Cron.Internal;

internal static class ReminderCronTimeZoneResolver
{
    public static TimeZoneInfo Resolve(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            }

            throw;
        }
    }

    public static bool IsUtc(TimeZoneInfo zone)
        => ReferenceEquals(zone, TimeZoneInfo.Utc) || zone.HasSameRules(TimeZoneInfo.Utc);
}
