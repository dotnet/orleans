using System;
using System.Linq;
using System.Reflection;
using Orleans;
using Xunit;

namespace UnitTests.Reminders;

[TestCategory("Reminders")]
public class ReminderCronTests
{
    [Theory]
    [InlineData("@yearly", 2027, 1, 1, 0, 0, 0)]
    [InlineData("@annually", 2027, 1, 1, 0, 0, 0)]
    [InlineData("@monthly", 2026, 2, 1, 0, 0, 0)]
    [InlineData("@weekly", 2026, 1, 18, 0, 0, 0)]
    [InlineData("@daily", 2026, 1, 16, 0, 0, 0)]
    [InlineData("@midnight", 2026, 1, 16, 0, 0, 0)]
    [InlineData("@hourly", 2026, 1, 15, 11, 0, 0)]
    [InlineData("@every_minute", 2026, 1, 15, 10, 1, 0)]
    [InlineData("@every_second", 2026, 1, 15, 10, 0, 1)]
    public void Parse_Macros_ComputeExpectedNextOccurrence(
        string macro,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var expression = ReminderCronExpression.Parse(macro);
        var fromUtc = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_FiveFieldExpression_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("*/15 * * * *");
        var fromUtc = new DateTime(2026, 1, 1, 10, 7, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 10, 15, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_SixFieldExpression_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("10 */2 * * * *");
        var fromUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 10, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_MacroExpression_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("@daily");
        var fromUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData("0 9 L * *", 2026, 1, 1, 0, 0, 0, 2026, 1, 31, 9, 0, 0)]
    [InlineData("0 9 15W * *", 2026, 2, 1, 0, 0, 0, 2026, 2, 16, 9, 0, 0)]
    [InlineData("0 9 ? * MON#2", 2026, 1, 1, 0, 0, 0, 2026, 1, 12, 9, 0, 0)]
    [InlineData("0 9 ? * 5L", 2026, 1, 1, 0, 0, 0, 2026, 1, 30, 9, 0, 0)]
    public void Parse_AdvancedSyntax_ComputesExpectedNextOccurrence(
        string cron,
        int fromYear,
        int fromMonth,
        int fromDay,
        int fromHour,
        int fromMinute,
        int fromSecond,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var expression = ReminderCronExpression.Parse(cron);
        var fromUtc = new DateTime(fromYear, fromMonth, fromDay, fromHour, fromMinute, fromSecond, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_DayOfMonthAndDayOfWeek_UsesAndSemantics()
    {
        var expression = ReminderCronExpression.Parse("0 9 13 * MON");
        var fromUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 4, 13, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_SupportsMonthAndDayNamesCaseInsensitive()
    {
        var expression = ReminderCronExpression.Parse("0 30 9 ? jan mon");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData("55-5/5 * * * * *", 2026, 1, 1, 10, 0, 55)]
    [InlineData("55-5/5 * * * * *", 2026, 1, 1, 10, 1, 0)]
    public void Parse_ReversedRanges_ComputesExpectedNextOccurrence(
        string cron,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var expression = ReminderCronExpression.Parse(cron);
        var fromUtc = second == 55
            ? new DateTime(2026, 1, 1, 10, 0, 54, DateTimeKind.Utc)
            : new DateTime(2026, 1, 1, 10, 0, 56, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData("not-a-cron")]
    [InlineData("* * * *")]
    [InlineData("* * * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("* * * * MON#0")]
    [InlineData("* * * * MON#6")]
    [InlineData("*/0 * * * *")]
    [InlineData("@unknown")]
    [InlineData("0 0 0 1 1 ? 2026")]
    [InlineData("61 * * * * *")]
    [InlineData("* 60 * * * *")]
    [InlineData("* * 24 * * *")]
    [InlineData("* * * 0 * *")]
    [InlineData("* * * * 0 *")]
    [InlineData("* * * * * 8")]
    [InlineData("A B C D E")]
    [InlineData("0 0 * * JANUARY")]
    [InlineData("0 0 * * MONDAY")]
    public void Parse_InvalidExpression_ThrowsFormatException(string expression)
    {
        Assert.ThrowsAny<FormatException>(() => ReminderCronExpression.Parse(expression));
    }

    [Fact]
    public void TryParse_InvalidExpression_ReturnsFalse()
    {
        var result = ReminderCronExpression.TryParse("not-a-cron", out var expression);

        Assert.False(result);
        Assert.Null(expression);
    }

    [Fact]
    public void TryParse_ValidExpression_ReturnsParsedExpression()
    {
        var result = ReminderCronExpression.TryParse("0 */2 * * * *", out var expression);

        Assert.True(result);
        Assert.NotNull(expression);
        Assert.Equal("0 */2 * * * *", expression!.ToExpressionString());
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("* * * * * * *")]
    [InlineData("@unknown")]
    [InlineData("*/0 * * * *")]
    public void TryParse_InvalidExpressions_ReturnFalse(string expression)
    {
        var result = ReminderCronExpression.TryParse(expression, out var parsed);

        Assert.False(result);
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_EmptyExpression_ReturnsFalse()
    {
        Assert.False(ReminderCronExpression.TryParse(" ", out _));
    }

    [Fact]
    public void GetOccurrences_ReturnsExpectedRange()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = expression.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(3, occurrences.Length);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), occurrences[0]);
        Assert.Equal(new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc), occurrences[1]);
        Assert.Equal(new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Utc), occurrences[2]);
    }

    [Fact]
    public void GetOccurrences_RespectsInclusiveBoundaries()
    {
        var expression = ReminderCronExpression.Parse("0 * * * * *");
        var fromUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 1, 10, 3, 0, DateTimeKind.Utc);

        var defaultBoundaries = expression.GetOccurrences(fromUtc, toUtc).ToArray();
        var exclusiveStartInclusiveEnd = expression.GetOccurrences(fromUtc, toUtc, fromInclusive: false, toInclusive: true).ToArray();

        Assert.Equal(
            [
                new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 10, 2, 0, DateTimeKind.Utc),
            ],
            defaultBoundaries);

        Assert.Equal(
            [
                new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 10, 2, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 10, 3, 0, DateTimeKind.Utc),
            ],
            exclusiveStartInclusiveEnd);
    }

    [Fact]
    public void GetNextOccurrence_RespectsInclusiveFlag()
    {
        var expression = ReminderCronExpression.Parse("0 * * * * *");
        var fromUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var exclusive = expression.GetNextOccurrence(fromUtc, inclusive: false);
        var inclusive = expression.GetNextOccurrence(fromUtc, inclusive: true);

        Assert.Equal(new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc), exclusive);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), inclusive);
    }

    [Fact]
    public void GetNextOccurrence_WithTimeZone_UsesLocalScheduleAndReturnsUtc()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 6, 30, 0, DateTimeKind.Utc);
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            id: "UTC+02",
            baseUtcOffset: TimeSpan.FromHours(2),
            displayName: "UTC+02",
            standardDisplayName: "UTC+02");

        var next = expression.GetNextOccurrence(fromUtc, zone);

        Assert.Equal(new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetOccurrences_WithTimeZone_UsesLocalScheduleAndReturnsUtc()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            id: "UTC+02",
            baseUtcOffset: TimeSpan.FromHours(2),
            displayName: "UTC+02",
            standardDisplayName: "UTC+02");

        var occurrences = expression.GetOccurrences(fromUtc, toUtc, zone).ToArray();

        Assert.Equal(
            [
                new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 2, 7, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void GetNextOccurrence_WithTimeZone_ThrowsOnNullZone()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentNullException>(() => expression.GetNextOccurrence(fromUtc, zone: null!));
    }

    [Fact]
    public void GetOccurrences_ThrowsWhenFromIsAfterTo()
    {
        var expression = ReminderCronExpression.Parse("* * * * *");
        var fromUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentException>(() => expression.GetOccurrences(fromUtc, toUtc).ToArray());
    }

    [Fact]
    public void GetNextOccurrence_RequiresUtcDateTime()
    {
        var expression = ReminderCronExpression.Parse("* * * * *");
        var local = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() => expression.GetNextOccurrence(local));
    }

    [Fact]
    public void GetOccurrences_RequiresUtcDateTime()
    {
        var expression = ReminderCronExpression.Parse("* * * * *");
        var fromLocal = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var toUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentException>(() => expression.GetOccurrences(fromLocal, toUtc).ToArray());
    }

    [Fact]
    public void Parse_TrimsAndPreservesExpressionText()
    {
        var expression = ReminderCronExpression.Parse("  0 9 * * MON  ");

        Assert.Equal("0 9 * * MON", expression.ToExpressionString());
    }

    [Fact]
    public void Equality_IsTextBasedNotSemantic()
    {
        var left = ReminderCronExpression.Parse("0 9 * * MON");
        var rightSameText = ReminderCronExpression.Parse("0 9 * * MON");
        var rightSameScheduleDifferentText = ReminderCronExpression.Parse("0 9 * * 1");

        Assert.Equal(left, rightSameText);
        Assert.Equal(left.GetHashCode(), rightSameText.GetHashCode());
        Assert.NotEqual(left, rightSameScheduleDifferentText);
    }

    [Fact]
    public void Builder_ProvidesExpectedShortcuts()
    {
        Assert.Equal("* * * * *", ReminderCronBuilder.EveryMinute().ToExpressionString());
        Assert.Equal("5 * * * *", ReminderCronBuilder.HourlyAt(5).ToExpressionString());
        Assert.Equal("30 8 * * *", ReminderCronBuilder.DailyAt(8, 30).ToExpressionString());
        Assert.Equal("15 7 * * MON-FRI", ReminderCronBuilder.WeekdaysAt(7, 15).ToExpressionString());
        Assert.Equal("45 13 * * 0", ReminderCronBuilder.WeeklyOn(DayOfWeek.Sunday, 13, 45).ToExpressionString());
        Assert.Equal("10 14 20 * *", ReminderCronBuilder.MonthlyOn(20, 14, 10).ToExpressionString());
        Assert.Equal("0 6 L * *", ReminderCronBuilder.MonthlyOnLastDay(6, 0).ToExpressionString());
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, "0")]
    [InlineData(DayOfWeek.Monday, "1")]
    [InlineData(DayOfWeek.Tuesday, "2")]
    [InlineData(DayOfWeek.Wednesday, "3")]
    [InlineData(DayOfWeek.Thursday, "4")]
    [InlineData(DayOfWeek.Friday, "5")]
    [InlineData(DayOfWeek.Saturday, "6")]
    public void Builder_WeeklyOn_MapsAllDaysOfWeek(DayOfWeek dayOfWeek, string expectedCronDay)
    {
        var builder = ReminderCronBuilder.WeeklyOn(dayOfWeek, 9, 30);

        Assert.Equal($"30 9 * * {expectedCronDay}", builder.ToExpressionString());
    }

    [Fact]
    public void Builder_InvalidArguments_ThrowClearErrors()
    {
        Assert.Throws<ArgumentException>(() => ReminderCronBuilder.FromExpression(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(60));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(24, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 12, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOn(0, 12, 0));
    }

    [Fact]
    public void Builder_ToCronExpression_ValidatesExpression()
    {
        var builder = ReminderCronBuilder.FromExpression("*/5 * * * *");

        var expression = builder.ToCronExpression();

        Assert.Equal("*/5 * * * *", expression.ToExpressionString());
    }

    [Fact]
    public void Builder_FromExpression_TrimsWhitespace()
    {
        var builder = ReminderCronBuilder.FromExpression("   */5 * * * *   ");

        Assert.Equal("*/5 * * * *", builder.ToExpressionString());
    }

    [Fact]
    public void Builder_ToCronExpression_SupportsSixFieldExpressions()
    {
        var builder = ReminderCronBuilder.FromExpression("*/10 * * * * *");

        var expression = builder.ToCronExpression();

        Assert.Equal("*/10 * * * * *", expression.ToExpressionString());
    }

    [Fact]
    public void Builder_ToCronExpression_InvalidExpression_ThrowsFormatException()
    {
        var builder = ReminderCronBuilder.FromExpression("invalid");

        Assert.ThrowsAny<FormatException>(() => builder.ToCronExpression());
    }

    [Fact]
    public void Builder_Build_IsAliasToToCronExpression()
    {
        var builder = ReminderCronBuilder.FromExpression("*/10 * * * *");

        var fromBuild = builder.Build();
        var fromToCronExpression = builder.ToCronExpression();

        Assert.Equal(fromToCronExpression, fromBuild);
    }

    [Theory]
    [InlineData("0,5,10 * * * * *", "0,5,10 * * * * *")]
    [InlineData("* 0,15,30,45 * * * *", "* 0,15,30,45 * * * *")]
    [InlineData("0 0,12 0,23 1,15 1,12 0,7", "0 0,12 0,23 1,15 1,12 0")]
    public void CronExpression_ToString_CanonicalizesListsWithoutLooping(string input, string expectedCanonical)
    {
        var expression = ReminderCronExpression.Parse(input);

        var canonical = GetInternalCronExpressionText(expression);

        Assert.Equal(expectedCanonical, canonical);
    }

    [Fact]
    public void GetNextOccurrence_WhenSecondListWrapsMinute_RollsOverToZeroSecond()
    {
        var expression = ReminderCronExpression.Parse("0,30 * * * * *");
        var fromUtc = new DateTime(2026, 1, 1, 10, 0, 31, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetNextOccurrence_WhenDayOfMonthRollsOver_SelectsFirstDay()
    {
        var expression = ReminderCronExpression.Parse("0 0 0 1 * *");
        var fromUtc = new DateTime(2026, 1, 31, 23, 59, 59, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetNextOccurrence_WhenMonthRollsOver_SelectsJanuary()
    {
        var expression = ReminderCronExpression.Parse("0 0 0 1 1 *");
        var fromUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData(2024, 6, 1, 2024, 6, 3)] // 1st is Saturday -> move forward to Monday.
    [InlineData(2024, 9, 1, 2024, 9, 2)] // 1st is Sunday -> move forward to Monday.
    public void Parse_FirstDayNearestWeekday_HandlesWeekendAtStartOfMonth(
        int fromYear,
        int fromMonth,
        int fromDay,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        var expression = ReminderCronExpression.Parse("0 9 1W * *");
        var fromUtc = new DateTime(fromYear, fromMonth, fromDay, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(expectedYear, expectedMonth, expectedDay, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData(2026, 1, 1, 2026, 1, 30)] // Last day is Saturday -> Friday.
    [InlineData(2026, 5, 1, 2026, 5, 29)] // Last day is Sunday -> Friday.
    public void Parse_LastDayNearestWeekday_HandlesWeekendAtEndOfMonth(
        int fromYear,
        int fromMonth,
        int fromDay,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        var expression = ReminderCronExpression.Parse("0 9 LW * *");
        var fromUtc = new DateTime(fromYear, fromMonth, fromDay, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(expectedYear, expectedMonth, expectedDay, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_LastDayOffset_RespectsLeapYearLength()
    {
        var expression = ReminderCronExpression.Parse("0 9 L-1 * *");
        var fromUtc = new DateTime(2028, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2028, 2, 28, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_NthWeekday_WhenMissingInCurrentMonth_SkipsToNextMatchingMonth()
    {
        var expression = ReminderCronExpression.Parse("0 9 ? * MON#5");
        var fromUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 3, 30, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_SundayAliases_SevenAndZeroHaveSameScheduleAndCanonicalForm()
    {
        var zeroSunday = ReminderCronExpression.Parse("0 9 ? * 0");
        var sevenSunday = ReminderCronExpression.Parse("0 9 ? * 7");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var zeroNext = zeroSunday.GetNextOccurrence(fromUtc);
        var sevenNext = sevenSunday.GetNextOccurrence(fromUtc);

        Assert.Equal(zeroNext, sevenNext);
        Assert.Equal("0 9 * * 0", GetInternalCronExpressionText(sevenSunday));
    }

    [Fact]
    public void Parse_ReversedDayOfWeekRangeWithStep_WrapsAcrossSundayCorrectly()
    {
        var expression = ReminderCronExpression.Parse("0 9 ? * 5-1/2");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); // Thursday

        var first = expression.GetNextOccurrence(fromUtc);
        var second = expression.GetNextOccurrence(first!.Value.AddSeconds(1));

        Assert.Equal(new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc), first); // Friday
        Assert.Equal(new DateTime(2026, 1, 4, 9, 0, 0, DateTimeKind.Utc), second); // Sunday
    }

    [Fact]
    public void GetNextOccurrence_NearDateTimeMaxValue_ReturnsNullWhenNoFutureMatch()
    {
        var expression = ReminderCronExpression.Parse("0 0 1 1 *");
        var fromUtc = new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Null(next);
    }

    [Fact]
    public void GetOccurrences_WhenBoundsEqualAndBothInclusive_ReturnsSingleOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 * * * * *");
        var atUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var occurrences = expression.GetOccurrences(atUtc, atUtc, fromInclusive: true, toInclusive: true).ToArray();

        Assert.Equal([atUtc], occurrences);
    }

    private static string GetInternalCronExpressionText(ReminderCronExpression expression)
    {
        var field = typeof(ReminderCronExpression).GetField("_expression", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var internalExpression = field!.GetValue(expression);
        Assert.NotNull(internalExpression);

        return internalExpression!.ToString()!;
    }
}
