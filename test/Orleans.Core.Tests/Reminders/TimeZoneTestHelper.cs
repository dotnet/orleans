using System;

namespace NonSilo.Tests.Reminders;

internal static class TimeZoneTestHelper
{
    public static TimeZoneInfo GetDubaiTimeZone()
        => ResolveTimeZone("Asia/Dubai", "Arabian Standard Time");

    public static TimeZoneInfo GetUsEasternTimeZone()
        => ResolveTimeZone("America/New_York", "Eastern Standard Time");

    public static TimeZoneInfo GetCentralEuropeanTimeZone()
        => ResolveTimeZone("Europe/Berlin", "W. Europe Standard Time");

    public static TimeZoneInfo GetIndiaTimeZone()
        => ResolveTimeZone("Asia/Kolkata", "India Standard Time");

    public static TimeZoneInfo GetNepalTimeZone()
        => ResolveTimeZone("Asia/Kathmandu", "Nepal Standard Time");

    public static TimeZoneInfo GetLordHoweTimeZone()
        => ResolveTimeZone("Australia/Lord_Howe", "Lord Howe Standard Time");

    public static string GetCentralEuropeanAlternateTimeZoneId()
    {
        var zone = GetCentralEuropeanTimeZone();
        return string.Equals(zone.Id, "Europe/Berlin", StringComparison.Ordinal)
            ? "W. Europe Standard Time"
            : "Europe/Berlin";
    }

    private static TimeZoneInfo ResolveTimeZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            if (TryFindTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new InvalidOperationException($"Could not resolve any of the requested time zones: {string.Join(", ", ids)}.");
    }

    private static bool TryFindTimeZoneById(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
            {
                return TryFindTimeZoneById(windowsId, out zone);
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
            {
                return TryFindTimeZoneById(ianaId, out zone);
            }

            zone = null!;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = null!;
            return false;
        }
    }
}
