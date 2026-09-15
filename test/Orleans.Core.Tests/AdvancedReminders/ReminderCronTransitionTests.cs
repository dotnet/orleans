using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronTransitionTests
{
    [Theory]
    [InlineData("America/New_York", 2025, 3, 9)]
    [InlineData("America/New_York", 2025, 11, 2)]
    [InlineData("Europe/Kyiv", 2025, 3, 30)]
    [InlineData("Europe/Kyiv", 2025, 10, 26)]
    [InlineData("Australia/Lord_Howe", 2025, 4, 5)]
    [InlineData("Australia/Lord_Howe", 2025, 10, 4)]
    [InlineData("Pacific/Apia", 2011, 12, 30)]
    public void IntervalOccurrences_MatchIndependentUtcScan(string zoneId, int year, int month, int day)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var from = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        var until = from.AddDays(2);
        var expected = new List<DateTime>();
        for (var cursor = from; cursor < until; cursor = cursor.AddMinutes(1))
        {
            if (TimeZoneInfo.ConvertTimeFromUtc(cursor, zone).Minute % 15 == 0)
            {
                expected.Add(cursor);
            }
        }

        var actual = ReminderCronExpression.Parse("*/15 * * * *").GetOccurrences(from, until, zone).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SkippedCalendarDate_CatchesUpAtFirstValidInstant()
    {
        var zone = ReminderCronTestTimeZones.Resolve("Test/SkippedDate");
        var expression = ReminderCronExpression.Parse("0 9 30 12 *");
        Assert.Equal(new DateTime(2011, 12, 30, 10, 0, 0, DateTimeKind.Utc),
            expression.GetNextOccurrence(new DateTime(2011, 12, 29, 20, 0, 0, DateTimeKind.Utc), zone));
    }

    [Fact]
    public void CustomZoneNamedUtc_UsesItsActualOffset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC", TimeSpan.FromHours(3), "custom", "custom");
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        Assert.Equal(new DateTime(2025, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            builder.GetNextOccurrence(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void DailySchedule_DoesNotRepeatDuringFallBack()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var expression = ReminderCronExpression.Parse("30 1 * * *");
        var first = new DateTime(2025, 11, 2, 5, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2025, 11, 3, 6, 30, 0, DateTimeKind.Utc), expression.GetNextOccurrence(first, zone));
        Assert.Equal(first, expression.GetNextOccurrence(first, zone, inclusive: true));
    }

    [Fact]
    public void SpringGap_CatchesUpOnceThenAdvances()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var expression = ReminderCronExpression.Parse("0,15,30,45 2 * * *");
        var first = new DateTime(2025, 3, 9, 7, 0, 0, DateTimeKind.Utc);
        Assert.Equal(first, expression.GetNextOccurrence(first.AddHours(-1), zone));
        Assert.Equal(new DateTime(2025, 3, 10, 6, 0, 0, DateTimeKind.Utc), expression.GetNextOccurrence(first, zone));
    }

    [Theory]
    [InlineData(-14)]
    [InlineData(14)]
    public void DateTimeLimits_DoNotOverflow(int offset)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixed", TimeSpan.FromHours(offset), "fixed", "fixed");
        var expression = ReminderCronExpression.Parse("* * * * * *");
        Assert.NotNull(expression.GetNextOccurrence(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), zone, inclusive: true));
        Assert.Null(expression.GetNextOccurrence(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), zone));
    }
}
