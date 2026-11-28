using System.Globalization;

namespace UnitTests.AdvancedReminders;

public static class ReminderCronAllocationTestCases
{
    public static IEnumerable<object[]> Occurrences
    {
        get
        {
            yield return Case("* * * * * *", "UTC", "2025-11-02T05:00:00Z", "2025-11-02T05:00:01Z");
            yield return Case("0 0 29 2 *", "UTC", "2025-11-02T05:00:00Z", "2028-02-29T00:00:00Z");
            yield return Case("0 0 31 2 *", "UTC", "2025-11-02T05:00:00Z", null);
            yield return Case("*/15 * * * *", "America/New_York", "2025-11-02T05:00:00Z", "2025-11-02T05:15:00Z");
            yield return Case("30 1 * * *", "America/New_York", "2025-11-02T05:00:00Z", "2025-11-02T05:30:00Z");

            // European gaps, repeated clocks, and a query after the first daily occurrence.
            yield return Case("30 2 * * *", "Europe/Paris", "2025-03-30T00:00:00Z", "2025-03-30T01:00:00Z");
            yield return Case("30 2 * * *", "Europe/Paris", "2025-10-26T00:00:00Z", "2025-10-26T00:30:00Z");
            yield return Case("30 2 * * *", "Europe/Paris", "2025-10-26T00:45:00Z", "2025-10-27T01:30:00Z");
            yield return Case("30 2 * * *", "Europe/Vienna", "2025-03-30T00:00:00Z", "2025-03-30T01:00:00Z");
            yield return Case("*/15 * * * *", "Europe/Vienna", "2025-10-26T00:55:00Z", "2025-10-26T01:00:00Z");
            yield return Case("30 3 * * *", "Europe/Kyiv", "2025-03-30T00:00:00Z", "2025-03-30T01:00:00Z");
            yield return Case("30 3 * * *", "Europe/Kyiv", "2025-10-26T00:00:00Z", "2025-10-26T00:30:00Z");

            // Southern hemisphere seasons, including Lord Howe's half-hour transitions.
            yield return Case("30 2 * * *", "Australia/Sydney", "2025-10-04T15:00:00Z", "2025-10-04T16:00:00Z");
            yield return Case("30 2 * * *", "Australia/Sydney", "2025-04-05T15:00:00Z", "2025-04-05T15:30:00Z");
            yield return Case("45 1 * * *", "Australia/Lord_Howe", "2025-04-05T14:00:00Z", "2025-04-05T14:45:00Z");
            yield return Case("15 2 * * *", "Australia/Lord_Howe", "2025-10-04T15:00:00Z", "2025-10-04T15:30:00Z");
            yield return Case("*/15 * * * *", "Australia/Lord_Howe", "2025-04-05T14:55:00Z", "2025-04-05T15:00:00Z");

            // No seasonal clock change: local midnight, leap day, and fractional offsets.
            yield return Case("*/15 * * * *", "Asia/Dubai", "2025-11-01T19:55:00Z", "2025-11-01T20:00:00Z");
            yield return Case("30 1 * * *", "Asia/Dubai", "2025-11-01T21:00:00Z", "2025-11-01T21:30:00Z");
            yield return Case("0 0 29 2 *", "Asia/Dubai", "2024-02-28T19:59:00Z", "2024-02-28T20:00:00Z");
            yield return Case("0 0 * * *", "Asia/Kathmandu", "2025-11-01T18:00:00Z", "2025-11-01T18:15:00Z");
            yield return Case("0 0 29 2 *", "Asia/Kathmandu", "2024-02-28T18:00:00Z", "2024-02-28T18:15:00Z");
            yield return Case("0 0 * * *", "Australia/Eucla", "2025-11-01T15:00:00Z", "2025-11-01T15:15:00Z");
            yield return Case("0 0 * * *", "Asia/Kolkata", "2025-11-01T18:00:00Z", "2025-11-01T18:30:00Z");
            yield return Case("0 0 * * *", "Pacific/Marquesas", "2025-11-02T09:00:00Z", "2025-11-02T09:30:00Z");
            yield return Case("0 0 * * *", "Pacific/Kiritimati", "2025-11-01T09:00:00Z", "2025-11-01T10:00:00Z");

            // Quarter-hour base offsets, Ramadan changes, two-hour gaps, and a skipped date.
            yield return Case("15 3 * * *", "Pacific/Chatham", "2025-09-27T13:00:00Z", "2025-09-27T14:00:00Z");
            yield return Case("15 3 * * *", "Pacific/Chatham", "2025-04-05T13:00:00Z", "2025-04-05T13:30:00Z");
            yield return Case("30 2 * * *", "Africa/Casablanca", "2025-02-23T00:00:00Z", "2025-02-23T01:30:00Z");
            yield return Case("30 2 * * *", "Africa/Casablanca", "2025-04-06T01:00:00Z", "2025-04-06T02:00:00Z");
            yield return Case("30 1 * * *", "Test/TwoHourSeason", "2025-03-30T00:00:00Z", "2025-03-30T01:00:00Z");
            yield return Case("30 2 * * *", "Test/TwoHourSeason", "2025-10-26T00:00:00Z", "2025-10-26T00:30:00Z");
            yield return Case("0 0 * * *", "Test/SkippedDate", "2011-12-30T09:00:00Z", "2011-12-30T10:00:00Z");
        }
    }

    private static object[] Case(string expression, string zoneId, string fromUtc, string? expectedUtc)
        => [expression, zoneId, ParseUtc(fromUtc), expectedUtc is null ? null! : ParseUtc(expectedUtc)];

    private static DateTime ParseUtc(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
