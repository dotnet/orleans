using System.Globalization;
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronNonSeasonalZoneTests
{
    public static IEnumerable<object[]> CalendarCases()
    {
        foreach (var zone in new[] { "UTC", "Asia/Kathmandu", "Asia/Kolkata", "Australia/Eucla", "Pacific/Marquesas", "Pacific/Kiritimati", "America/Phoenix" })
            foreach (var year in new[] { 2024, 2100, 2400 })
                foreach (var hour in new[] { 0, 12, 23 })
                {
                    yield return [zone, year, hour];
                }
    }

    [Theory]
    [MemberData(nameof(CalendarCases))]
    public void DayAndNightSchedules_InNonSeasonalZones_MatchCalendarConversion(string zoneId, int year, int hour)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var expression = ReminderCronBuilder.FromExpression($"17 45 {hour.ToString(CultureInfo.InvariantCulture)} * * *", zone);
        var from = new DateTime(year, 2, 28, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(year, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        var expected = new List<DateTime>();
        for (var day = from.Date.AddDays(-1); day <= until.Date.AddDays(1); day = day.AddDays(1))
        {
            var local = DateTime.SpecifyKind(day.AddHours(hour).AddMinutes(45).AddSeconds(17), DateTimeKind.Unspecified);
            Assert.False(zone.IsInvalidTime(local));
            Assert.False(zone.IsAmbiguousTime(local));
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
            if (utc >= from && utc < until) expected.Add(utc);
        }

        Assert.Equal(expected, expression.GetOccurrences(from, until));
    }

    [Theory]
    [InlineData(1896, 1904)]
    [InlineData(1996, 2000)]
    [InlineData(2096, 2104)]
    [InlineData(2396, 2400)]
    public void LeapDayNightSchedule_RespectsGregorianCenturyRules(int afterLeapYear, int nextLeapYear)
    {
        var expression = ReminderCronExpression.Parse("59 59 23 29 2 *");
        var from = new DateTime(afterLeapYear, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(nextLeapYear, 2, 29, 23, 59, 59, DateTimeKind.Utc), expression.GetNextOccurrence(from));
    }
}
