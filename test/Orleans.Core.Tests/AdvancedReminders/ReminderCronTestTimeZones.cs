namespace UnitTests.AdvancedReminders;

/// <summary>Portable transition fixtures where Windows and IANA databases do not provide equivalent rules.</summary>
internal static class ReminderCronTestTimeZones
{
    public static TimeZoneInfo Resolve(string id) => id switch
    {
        "Test/TwoHourSeason" => CreateTwoHourSeason(),
        _ => TimeZoneInfo.FindSystemTimeZoneById(id),
    };

    private static TimeZoneInfo CreateTwoHourSeason()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 30);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 26);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), TimeSpan.FromHours(2), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test/TwoHourSeason", TimeSpan.Zero, "Two hour season", "Standard", "Adjusted", [rule]);
    }
}
