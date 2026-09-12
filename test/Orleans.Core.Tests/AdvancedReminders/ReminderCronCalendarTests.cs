using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronCalendarTests
{
    [Theory]
    [InlineData("0 0 1W * *", 2025, 6, 2)]
    [InlineData("0 0 31W * *", 2024, 3, 29)]
    [InlineData("0 0 1W * *", 2025, 2, 3)]
    [InlineData("0 0 14W * *", 2025, 1, 14)]
    [InlineData("0 0 ? * MON#2", 2026, 2, 9)]
    [InlineData("0 0 ? * FRIL", 2025, 1, 31)]
    public void CalendarRules_RespectMonthBoundaries(string expression, int year, int month, int day)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var expected = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ReminderCronExpression.Parse(expression).GetNextOccurrence(start, inclusive: true));
    }

    [Theory]
    [InlineData("0 0 31 2 *")]
    [InlineData("0 0 30 2 *")]
    [InlineData("0 0 L-30 2 *")]
    public void ImpossibleDates_ReturnNoOccurrence(string expression)
        => Assert.Null(ReminderCronExpression.Parse(expression).GetNextOccurrence(DateTime.UnixEpoch));

    [Theory]
    [InlineData("0", 0, 7)]
    [InlineData("7", 0, 7)]
    [InlineData("FRI-MON/2", 0, 5)]
    [InlineData("0-7", 0, 1)]
    public void SundayAliasesAndWrappingSteps_UseASevenDayWeek(string weekday, int firstDay, int secondDay)
    {
        var expression = ReminderCronExpression.Parse($"0 0 * * {weekday}");
        var sunday = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        var occurrences = expression.GetOccurrences(sunday, sunday.AddDays(8)).Take(2).ToArray();
        Assert.Equal([sunday.AddDays(firstDay), sunday.AddDays(secondDay)], occurrences);
    }

    [Fact]
    public void TruncatedSearchAtDateTimeLimit_DoesNotInvalidateEarlierOccurrences()
    {
        var expression = ReminderCronExpression.Parse("0 0 29 2 *");
        Assert.Null(expression.GetNextOccurrence(new DateTime(9999, 12, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc),
            expression.GetNextOccurrence(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void LeapDay_RespectsCenturyExceptions()
    {
        var expression = ReminderCronExpression.Parse("0 0 29 2 *");
        Assert.Equal(new DateTime(2104, 2, 29, 0, 0, 0, DateTimeKind.Utc),
            expression.GetNextOccurrence(new DateTime(2099, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
    }
}
