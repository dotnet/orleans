namespace UnitTests.AdvancedReminders;

/// <summary>Portable transition fixtures where Windows and IANA databases do not provide equivalent rules.</summary>
internal static class ReminderCronTestTimeZones
{
    public static TimeZoneInfo Resolve(string id) => id switch
    {
        "Test/TwoHourSeason" => CreateTwoHourSeason(),
        "Test/SkippedDate" => CreateSkippedDate(),
        _ => TimeZoneInfo.FindSystemTimeZoneById(id),
    };

    private static TimeZoneInfo CreateTwoHourSeason()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 30);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 26);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), TimeSpan.FromHours(2), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test/TwoHourSeason", TimeSpan.Zero, "Two hour season", "Standard", "Adjusted", [rule]);
    }

    private static TimeZoneInfo CreateSkippedDate()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(DateTime.MinValue, 1, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(DateTime.MinValue, 12, 31);
        // The rule's date boundary is evaluated using the zone's base offset (+14).
        // UTC 2011-12-30 10:00 jumps from local Dec 29 23:59:59 (-10) to Dec 31 00:00 (+14).
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2011, 1, 1), new DateTime(2011, 12, 30),
            TimeSpan.Zero, start, end, TimeSpan.FromHours(-24));
        return TimeZoneInfo.CreateCustomTimeZone("Test/SkippedDate", TimeSpan.FromHours(14), "Skipped date", "Before", "After", [rule]);
    }
}
