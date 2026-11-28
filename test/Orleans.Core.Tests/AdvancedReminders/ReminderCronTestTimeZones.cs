namespace UnitTests.AdvancedReminders;

/// <summary>Portable transition fixtures where Windows and IANA databases do not provide equivalent rules.</summary>
internal static class ReminderCronTestTimeZones
{
    // TimeZoneInfo.ToSerializedString output for a shift from UTC-10 to UTC+14 at
    // 2011-12-30 10:00 UTC. The trailing extension preserves absolute rule dates;
    // the preceding compatibility rule represents the same transition on .NET 10.
    private const string SerializedSkippedDate =
        "Test/SkippedDate;840;Skipped date;Before;After;[01:01:2011;12:30:2011;0;[1;14:00:00;1;1;];[1;23:59:59.999;12;30;];-1440;];" +
        "!1;1;634294368000000000;1;634608359999999999;1;0;-864000000000;1;D;D;";

    public static TimeZoneInfo Resolve(string id) => id switch
    {
        "Test/TwoHourSeason" => CreateTwoHourSeason(),
        "Test/SkippedDate" => TimeZoneInfo.FromSerializedString(SerializedSkippedDate),
        "Test/YearBoundary" => CreateYearBoundary(),
        _ => TimeZoneInfo.FindSystemTimeZoneById(id),
    };

    private static TimeZoneInfo CreateTwoHourSeason()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 30);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 26);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), TimeSpan.FromHours(2), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test/TwoHourSeason", TimeSpan.Zero, "Two hour season", "Standard", "Adjusted", [rule]);
    }

    private static TimeZoneInfo CreateYearBoundary()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(DateTime.MinValue, 1, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(DateTime.MinValue, 12, 31);
        var boundary = new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(boundary.AddYears(-1), boundary.AddTicks(-1),
            TimeSpan.Zero, start, end, TimeSpan.FromHours(-24));
        return TimeZoneInfo.CreateCustomTimeZone("Test/YearBoundary", TimeSpan.FromHours(14), "Year boundary", "Standard", "Adjusted", [rule]);
    }
}
