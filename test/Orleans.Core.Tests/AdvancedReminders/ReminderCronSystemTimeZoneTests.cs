using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronSystemTimeZoneTests
{
    [Theory]
    [InlineData(2025, 2, 23)]
    [InlineData(2025, 3, 9)]
    [InlineData(2025, 3, 30)]
    [InlineData(2025, 4, 5)]
    [InlineData(2025, 4, 6)]
    [InlineData(2025, 10, 4)]
    [InlineData(2025, 10, 26)]
    [InlineData(2025, 11, 2)]
    public void AllSystemZones_QuarterHourSchedulesMatchUtcTimeline(int year, int month, int day)
    {
        var from = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        var until = from.AddDays(1);
        var expression = ReminderCronExpression.Parse("*/15 * * * *");
        var zones = TimeZoneInfo.GetSystemTimeZones();
        Assert.NotEmpty(zones);
        foreach (var zone in zones)
        {
            AssertMatchesTimeline(expression, zone, from, until);
        }
    }

    [Theory]
    [InlineData(15)]
    [InlineData(-15)]
    public void TwoTransitionsWithinOneHour_MatchUtcTimeline(int daylightMinutes)
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 6, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 45, 0), 6, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31),
            TimeSpan.FromMinutes(daylightMinutes), start, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone("short adjustment", TimeSpan.Zero, "short adjustment", "standard", "adjusted", [rule]);
        var from = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        AssertMatchesTimeline(ReminderCronExpression.Parse("*/15 * * * *"), zone, from, from.AddHours(3));
    }

    private static void AssertMatchesTimeline(ReminderCronExpression expression, TimeZoneInfo zone, DateTime from, DateTime until)
    {
        var expected = new List<DateTime>();
        for (var cursor = from; cursor < until; cursor = cursor.AddMinutes(1))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(cursor, zone);
            if (local.Minute % 15 == 0)
            {
                expected.Add(cursor);
            }
        }

        var actual = expression.GetOccurrences(from, until, zone).ToArray();
        if (!expected.SequenceEqual(actual))
        {
            Assert.Fail($"Time zone {zone.Id}, UTC range {from:O}..{until:O}. Expected: {string.Join(", ", expected.Select(value => value.ToString("O")))}. Actual: {string.Join(", ", actual.Select(value => value.ToString("O")))}.");
        }
    }
}
