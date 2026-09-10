#nullable enable
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronBuilderTests
{
    [Fact]
    public void Builder_TypedFields_CoverBaseCronGrammar()
    {
        Assert.Equal("* * * * *", Five().ToExpressionString());

        Assert.Equal("10,30 * * * *", Five(minute: ReminderCronMinute.At(30, 10, 30)).ToExpressionString());
        Assert.Equal("55-5 * * * *", Five(minute: ReminderCronMinute.Range(55, 5)).ToExpressionString());
        Assert.Equal("*/5 * * * *", Five(minute: ReminderCronMinute.Every(5)).ToExpressionString());
        Assert.Equal("10/20 * * * *", Five(minute: ReminderCronMinute.EveryFrom(10, 20)).ToExpressionString());
        Assert.Equal("5-15/5 * * * *", Five(minute: ReminderCronMinute.EveryBetween(5, 15, 5)).ToExpressionString());
        Assert.Equal(
            "3,5-11/3,12 * * * *",
            Five(minute: ReminderCronMinute.Combine(
                ReminderCronMinute.At(3),
                ReminderCronMinute.EveryBetween(5, 11, 3),
                ReminderCronMinute.At(12))).ToExpressionString());

        Assert.Equal("10,30 * * * * *", Six(second: ReminderCronSecond.At(30, 10, 30)).ToExpressionString());
        Assert.Equal("55-5 * * * * *", Six(second: ReminderCronSecond.Range(55, 5)).ToExpressionString());
        Assert.Equal("*/5 * * * * *", Six(second: ReminderCronSecond.Every(5)).ToExpressionString());
        Assert.Equal("10/20 * * * * *", Six(second: ReminderCronSecond.EveryFrom(10, 20)).ToExpressionString());
        Assert.Equal("5-15/5 * * * * *", Six(second: ReminderCronSecond.EveryBetween(5, 15, 5)).ToExpressionString());
        Assert.Equal(
            "3,5-11/3,12 * * * * *",
            Six(second: ReminderCronSecond.Combine(
                ReminderCronSecond.At(3),
                ReminderCronSecond.EveryBetween(5, 11, 3),
                ReminderCronSecond.At(12))).ToExpressionString());

        Assert.Equal("* 6,14,16 * * *", Five(hour: ReminderCronHour.At(16, 6, 14)).ToExpressionString());
        Assert.Equal("* 22-1 * * *", Five(hour: ReminderCronHour.Range(22, 1)).ToExpressionString());
        Assert.Equal("* */4 * * *", Five(hour: ReminderCronHour.Every(4)).ToExpressionString());
        Assert.Equal("* 6/4 * * *", Five(hour: ReminderCronHour.EveryFrom(6, 4)).ToExpressionString());
        Assert.Equal("* 6-18/4 * * *", Five(hour: ReminderCronHour.EveryBetween(6, 18, 4)).ToExpressionString());
        Assert.Equal(
            "* 6,9-17/4,22 * * *",
            Five(hour: ReminderCronHour.Combine(
                ReminderCronHour.At(6),
                ReminderCronHour.EveryBetween(9, 17, 4),
                ReminderCronHour.At(22))).ToExpressionString());

        Assert.Equal("* * 1,15 * *", Five(dayOfMonth: ReminderCronDayOfMonth.On(15, 1)).ToExpressionString());
        Assert.Equal("* * 28-3 * *", Five(dayOfMonth: ReminderCronDayOfMonth.Range(28, 3)).ToExpressionString());
        Assert.Equal("* * */5 * *", Five(dayOfMonth: ReminderCronDayOfMonth.Every(5)).ToExpressionString());
        Assert.Equal("* * 3/5 * *", Five(dayOfMonth: ReminderCronDayOfMonth.EveryFrom(3, 5)).ToExpressionString());
        Assert.Equal("* * 3-18/5 * *", Five(dayOfMonth: ReminderCronDayOfMonth.EveryBetween(3, 18, 5)).ToExpressionString());
        Assert.Equal(
            "* * 1,5-15/5,20 * *",
            Five(dayOfMonth: ReminderCronDayOfMonth.Combine(
                ReminderCronDayOfMonth.On(1),
                ReminderCronDayOfMonth.EveryBetween(5, 15, 5),
                ReminderCronDayOfMonth.On(20))).ToExpressionString());

        Assert.Equal("* * * 1,3 *", Five(month: ReminderCronMonth.In(3, 1, 3)).ToExpressionString());
        Assert.Equal("* * * 12-2 *", Five(month: ReminderCronMonth.Range(12, 2)).ToExpressionString());
        Assert.Equal("* * * */3 *", Five(month: ReminderCronMonth.Every(3)).ToExpressionString());
        Assert.Equal("* * * 2/3 *", Five(month: ReminderCronMonth.EveryFrom(2, 3)).ToExpressionString());
        Assert.Equal("* * * 2-11/3 *", Five(month: ReminderCronMonth.EveryBetween(2, 11, 3)).ToExpressionString());
        Assert.Equal(
            "* * * 1,3-9/3,12 *",
            Five(month: ReminderCronMonth.Combine(
                ReminderCronMonth.In(1),
                ReminderCronMonth.EveryBetween(3, 9, 3),
                ReminderCronMonth.In(12))).ToExpressionString());

        Assert.Equal(
            "* * * * 1,3,5",
            Five(dayOfWeek: ReminderCronDayOfWeek.On(DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday)).ToExpressionString());
        Assert.Equal(
            "* * * * 5-1",
            Five(dayOfWeek: ReminderCronDayOfWeek.Range(DayOfWeek.Friday, DayOfWeek.Monday)).ToExpressionString());
        Assert.Equal("* * * * */2", Five(dayOfWeek: ReminderCronDayOfWeek.Every(2)).ToExpressionString());
        Assert.Equal(
            "* * * * 1/2",
            Five(dayOfWeek: ReminderCronDayOfWeek.EveryFrom(DayOfWeek.Monday, 2)).ToExpressionString());
        Assert.Equal(
            "* * * * 1-5/2",
            Five(dayOfWeek: ReminderCronDayOfWeek.EveryBetween(DayOfWeek.Monday, DayOfWeek.Friday, 2)).ToExpressionString());
        Assert.Equal(
            "* * * * 1,3-5,0",
            Five(dayOfWeek: ReminderCronDayOfWeek.Combine(
                ReminderCronDayOfWeek.On(DayOfWeek.Monday),
                ReminderCronDayOfWeek.Range(DayOfWeek.Wednesday, DayOfWeek.Friday),
                ReminderCronDayOfWeek.On(DayOfWeek.Sunday))).ToExpressionString());

        static ReminderCronBuilder Five(
            ReminderCronMinute? minute = null,
            ReminderCronHour? hour = null,
            ReminderCronDayOfMonth? dayOfMonth = null,
            ReminderCronMonth? month = null,
            ReminderCronDayOfWeek? dayOfWeek = null)
            => ReminderCronBuilder.FromFields(
                minute ?? ReminderCronMinute.Any,
                hour ?? ReminderCronHour.Any,
                dayOfMonth ?? ReminderCronDayOfMonth.Any,
                month ?? ReminderCronMonth.Any,
                dayOfWeek ?? ReminderCronDayOfWeek.Any);

        static ReminderCronBuilder Six(
            ReminderCronSecond? second = null,
            ReminderCronMinute? minute = null,
            ReminderCronHour? hour = null,
            ReminderCronDayOfMonth? dayOfMonth = null,
            ReminderCronMonth? month = null,
            ReminderCronDayOfWeek? dayOfWeek = null)
            => ReminderCronBuilder.FromFields(
                second ?? ReminderCronSecond.Any,
                minute ?? ReminderCronMinute.Any,
                hour ?? ReminderCronHour.Any,
                dayOfMonth ?? ReminderCronDayOfMonth.Any,
                month ?? ReminderCronMonth.Any,
                dayOfWeek ?? ReminderCronDayOfWeek.Any);
    }

    [Fact]
    public void Builder_TypedFields_CoverSpecialCronGrammar()
    {
        Assert.Equal("0 9 15W * *", Build(ReminderCronDayOfMonth.NearestWeekday(15)).ToExpressionString());
        Assert.Equal("0 9 L * *", Build(ReminderCronDayOfMonth.LastDay).ToExpressionString());
        Assert.Equal("0 9 L-3 * *", Build(ReminderCronDayOfMonth.DaysBeforeLast(3)).ToExpressionString());
        Assert.Equal("0 9 LW * *", Build(ReminderCronDayOfMonth.LastWeekday).ToExpressionString());
        Assert.Equal("0 9 L-5W * *", Build(ReminderCronDayOfMonth.NearestWeekdayBeforeLast(5)).ToExpressionString());
        Assert.Equal("0 9 * * 5L", Build(dayOfWeek: ReminderCronDayOfWeek.Last(DayOfWeek.Friday)).ToExpressionString());
        Assert.Equal("0 9 * * 1#2", Build(dayOfWeek: ReminderCronDayOfWeek.Nth(DayOfWeek.Monday, 2)).ToExpressionString());
        Assert.Equal(
            "0 9 13 * 5",
            Build(ReminderCronDayOfMonth.On(13), ReminderCronDayOfWeek.On(DayOfWeek.Friday)).ToExpressionString());

        static ReminderCronBuilder Build(
            ReminderCronDayOfMonth? dayOfMonth = null,
            ReminderCronDayOfWeek? dayOfWeek = null)
            => ReminderCronBuilder.FromFields(
                ReminderCronMinute.At(0),
                ReminderCronHour.At(9),
                dayOfMonth ?? ReminderCronDayOfMonth.Any,
                ReminderCronMonth.Any,
                dayOfWeek ?? ReminderCronDayOfWeek.Any);
    }

    [Fact]
    public void Builder_TypedFields_MatchRawExpressionsAcrossOneHundredOccurrences()
    {
        var cases = new (ReminderCronBuilder Builder, string RawExpression)[]
        {
            (ReminderCronBuilder.FromFields(
                ReminderCronSecond.Every(20),
                ReminderCronMinute.Any,
                ReminderCronHour.Any,
                ReminderCronDayOfMonth.Any,
                ReminderCronMonth.Any,
                ReminderCronDayOfWeek.Any), "*/20 * * * * *"),
            (ReminderCronBuilder.FromFields(
                ReminderCronMinute.Combine(
                    ReminderCronMinute.At(3),
                    ReminderCronMinute.EveryBetween(5, 11, 3),
                    ReminderCronMinute.At(12)),
                ReminderCronHour.At(1),
                ReminderCronDayOfMonth.Any,
                ReminderCronMonth.Any,
                ReminderCronDayOfWeek.Any), "3,5-11/3,12 1 * * *"),
            (ReminderCronBuilder.FromFields(
                ReminderCronMinute.At(0),
                ReminderCronHour.At(9),
                ReminderCronDayOfMonth.NearestWeekdayBeforeLast(5),
                ReminderCronMonth.Any,
                ReminderCronDayOfWeek.Any), "0 9 L-5W * *"),
            (ReminderCronBuilder.FromFields(
                ReminderCronMinute.At(0),
                ReminderCronHour.At(9),
                ReminderCronDayOfMonth.On(13),
                ReminderCronMonth.Any,
                ReminderCronDayOfWeek.On(DayOfWeek.Friday)), "0 9 13 * 5"),
        };
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var (builder, rawExpression) in cases)
        {
            var expected = ReminderCronBuilder.FromExpression(rawExpression)
                .GetOccurrences(fromUtc, toUtc)
                .Take(100)
                .ToArray();
            var actual = builder.GetOccurrences(fromUtc, toUtc).Take(100).ToArray();

            Assert.Equal(100, actual.Length);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Builder_TypedFields_RejectInvalidDefinitions()
    {
        Assert.Throws<ArgumentException>(() => ReminderCronMinute.At([]));
        Assert.Throws<ArgumentNullException>(() => ReminderCronMinute.At(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronSecond.At(60));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronMinute.Range(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronHour.Every(25));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfMonth.On(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfMonth.DaysBeforeLast(31));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfMonth.NearestWeekdayBeforeLast(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronMonth.In(13));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.On((DayOfWeek)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.Nth(DayOfWeek.Monday, 6));
        Assert.Throws<ArgumentException>(() => ReminderCronMinute.Combine([]));
        Assert.Throws<ArgumentException>(() => ReminderCronMinute.Combine(ReminderCronMinute.Any, ReminderCronMinute.At(5)));
        Assert.Throws<ArgumentException>(() => ReminderCronDayOfMonth.Combine(ReminderCronDayOfMonth.LastDay, ReminderCronDayOfMonth.On(15)));
        Assert.Throws<ArgumentNullException>(() => ReminderCronBuilder.FromFields(
            null!,
            ReminderCronHour.Any,
            ReminderCronDayOfMonth.Any,
            ReminderCronMonth.Any,
            ReminderCronDayOfWeek.Any));
    }

    [Fact]
    public void Builder_FactoryHelpers_EmitExpectedExpressions()
    {
        Assert.Equal("* * * * * *", ReminderCronBuilder.EverySecond().ToExpressionString());
        Assert.Equal("*/5 * * * * *", ReminderCronBuilder.EverySeconds(5).ToExpressionString());
        Assert.Equal("* * * * *", ReminderCronBuilder.EveryMinute().ToExpressionString());
        Assert.Equal("15 * * * *", ReminderCronBuilder.HourlyAt(15).ToExpressionString());
        Assert.Equal("10 15 * * * *", ReminderCronBuilder.HourlyAt(15, 10).ToExpressionString());
        Assert.Equal("0 9 * * *", ReminderCronBuilder.DailyAt(9, 0).ToExpressionString());
        Assert.Equal("15 30 9 * * *", ReminderCronBuilder.DailyAt(9, 30, 15).ToExpressionString());
        Assert.Equal("30 9 * * MON-FRI", ReminderCronBuilder.WeekdaysAt(9, 30).ToExpressionString());
        Assert.Equal("15 30 9 * * MON-FRI", ReminderCronBuilder.WeekdaysAt(9, 30, 15).ToExpressionString());
        Assert.Equal("30 9 * * SAT,SUN", ReminderCronBuilder.WeekendsAt(9, 30).ToExpressionString());
        Assert.Equal("5 4 * * 1", ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 4, 5).ToExpressionString());
        Assert.Equal("6 5 4 * * 1", ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 4, 5, 6).ToExpressionString());
        Assert.Equal("59 23 31 * *", ReminderCronBuilder.MonthlyOn(31, 23, 59).ToExpressionString());
        Assert.Equal("58 59 23 31 * *", ReminderCronBuilder.MonthlyOn(31, 23, 59, 58).ToExpressionString());
        Assert.Equal("59 23 L * *", ReminderCronBuilder.MonthlyOnLastDay(23, 59).ToExpressionString());
        Assert.Equal("58 59 23 L * *", ReminderCronBuilder.MonthlyOnLastDay(23, 59, 58).ToExpressionString());
        Assert.Equal("45 6 15 3 *", ReminderCronBuilder.YearlyOn(3, 15, 6, 45).ToExpressionString());
        Assert.Equal("30 45 6 15 3 *", ReminderCronBuilder.YearlyOn(3, 15, 6, 45, 30).ToExpressionString());
    }

    [Fact]
    public void Builder_AdvancedFactoryHelpers_EmitValidatedExpressions()
    {
        var builders = new (ReminderCronBuilder Builder, string Expression)[]
        {
            (ReminderCronBuilder.EveryMinutes(5), "*/5 * * * *"),
            (ReminderCronBuilder.EveryMinuteAtSecond(15), "15 * * * * *"),
            (ReminderCronBuilder.WeeklyOn(
                [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Monday],
                new TimeOnly(9, 0)), "0 9 * * 1,3,5"),
            (ReminderCronBuilder.MonthlyOnNearestWeekday(1, new TimeOnly(9, 0)), "0 9 1W * *"),
            (ReminderCronBuilder.MonthlyBeforeLastDay(3, new TimeOnly(9, 0)), "0 9 L-3 * *"),
            (ReminderCronBuilder.MonthlyOnLast(DayOfWeek.Friday, new TimeOnly(9, 0)), "0 9 ? * 5L"),
            (ReminderCronBuilder.MonthlyOnNth(DayOfWeek.Monday, 2, new TimeOnly(9, 0)), "0 9 ? * 1#2"),
            (ReminderCronBuilder.WeekdaysInMonthsAt([3, 1, 3], new TimeOnly(9, 30, 15)), "15 30 9 ? 1,3 MON-FRI"),
        };

        foreach (var (builder, expression) in builders)
        {
            Assert.Equal(expression, builder.ToExpressionString());
            Assert.Equal(expression, builder.Build().ToExpressionString());
        }
    }

    [Fact]
    public void Builder_TimeOnlyAndTimeSpanHelpers_EmitExpectedExpressions()
    {
        Assert.Equal("15 * * * *", ReminderCronBuilder.HourlyAt(TimeSpan.FromMinutes(15)).ToExpressionString());
        Assert.Equal("30 9 * * *", ReminderCronBuilder.DailyAt(new TimeOnly(9, 30)).ToExpressionString());
        Assert.Equal("15 30 9 * * MON-FRI", ReminderCronBuilder.WeekdaysAt(new TimeSpan(9, 30, 15)).ToExpressionString());
        Assert.Equal("15 30 9 * * SAT,SUN", ReminderCronBuilder.WeekendsAt(new TimeSpan(9, 30, 15)).ToExpressionString());
        Assert.Equal("6 5 4 * * 2", ReminderCronBuilder.WeeklyOn(DayOfWeek.Tuesday, new TimeOnly(4, 5, 6)).ToExpressionString());
        Assert.Equal("59 23 31 * *", ReminderCronBuilder.MonthlyOn(31, TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)).ToExpressionString());
        Assert.Equal("58 59 23 L * *", ReminderCronBuilder.MonthlyOnLastDay(new TimeOnly(23, 59, 58)).ToExpressionString());
        Assert.Equal("34 12 29 2 *", ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), new TimeOnly(12, 34)).ToExpressionString());
        Assert.Equal("34 12 29 2 *", ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), 12, 34).ToExpressionString());
        Assert.Equal("56 34 12 29 2 *", ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), 12, 34, 56).ToExpressionString());
    }

    [Fact]
    public void Builder_YearlyOn_DateOnly_IgnoresYear()
    {
        var first = ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), new TimeOnly(12, 34));
        var second = ReminderCronBuilder.YearlyOn(new DateOnly(2032, 2, 29), new TimeOnly(12, 34));

        Assert.Equal(first.ToExpressionString(), second.ToExpressionString());
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, 0)]
    [InlineData(DayOfWeek.Monday, 1)]
    [InlineData(DayOfWeek.Tuesday, 2)]
    [InlineData(DayOfWeek.Wednesday, 3)]
    [InlineData(DayOfWeek.Thursday, 4)]
    [InlineData(DayOfWeek.Friday, 5)]
    [InlineData(DayOfWeek.Saturday, 6)]
    public void Builder_WeeklyOn_MapsDayOfWeekToCronValue(DayOfWeek dayOfWeek, int expectedCronDay)
    {
        var builder = ReminderCronBuilder.WeeklyOn(dayOfWeek, 4, 5);

        Assert.Equal($"5 4 * * {expectedCronDay}", builder.ToExpressionString());
    }

    [Fact]
    public void Builder_FromExpression_TrimsAndSupportsBuildAliases()
    {
        var builder = ReminderCronBuilder.FromExpression("  0 9 * * *  ");

        Assert.Equal("0 9 * * *", builder.ToExpressionString());
        Assert.Equal(TimeZoneInfo.Utc.Id, builder.TimeZone.Id);
        Assert.Equal("0 9 * * *", builder.ToCronExpression().ToExpressionString());
        Assert.Equal("0 9 * * *", builder.Build().ToExpressionString());
    }

    [Fact]
    public void Builder_FromExpression_WithUtcZone_UsesUtcBranchForNextOccurrence()
    {
        var builder = ReminderCronBuilder.FromExpression("0 9 * * *", TimeZoneInfo.Utc);
        var fromUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Builder_FromExpression_WithUtcZone_UsesUtcBranchForOccurrences()
    {
        var builder = ReminderCronBuilder.FromExpression("0 9 * * *", TimeZoneInfo.Utc);
        var fromUtc = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc, fromInclusive: false, toInclusive: true).ToArray();

        Assert.Equal(
            [
                new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void Builder_HourlyAt_InvalidMinute_Throws(int minute)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(minute));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(24, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 60)]
    public void Builder_DailyAt_InvalidClockValues_Throws(int hour, int minute)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(hour, minute));
    }

    [Fact]
    public void Builder_HourlyAt_InvalidOffset_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(TimeSpan.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void Builder_EverySeconds_InvalidInterval_Throws(int interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.EverySeconds(interval));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void Builder_EveryMinutes_InvalidInterval_Throws(int interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.EveryMinutes(interval));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void Builder_EveryMinuteAtSecond_InvalidSecond_Throws(int second)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.EveryMinuteAtSecond(second));
    }

    [Fact]
    public void Builder_WeeklyOnMultipleDays_RequiresValidNonEmptyDays()
    {
        Assert.Throws<ArgumentNullException>(() => ReminderCronBuilder.WeeklyOn(null!, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentException>(() => ReminderCronBuilder.WeeklyOn([], new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeeklyOn([(DayOfWeek)99], new TimeOnly(9, 0)));
    }

    [Fact]
    public void Builder_AdvancedMonthlyHelpers_RejectInvalidCalendarValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnNearestWeekday(0, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyBeforeLastDay(0, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyBeforeLastDay(31, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnLast((DayOfWeek)99, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnNth(DayOfWeek.Monday, 0, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnNth(DayOfWeek.Monday, 6, new TimeOnly(9, 0)));
    }

    [Fact]
    public void Builder_WeekdaysInMonthsAt_RequiresValidNonEmptyMonths()
    {
        Assert.Throws<ArgumentNullException>(() => ReminderCronBuilder.WeekdaysInMonthsAt(null!, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentException>(() => ReminderCronBuilder.WeekdaysInMonthsAt([], new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeekdaysInMonthsAt([0], new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeekdaysInMonthsAt([13], new TimeOnly(9, 0)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void Builder_SecondBasedHelpers_InvalidSecond_Throws(int second)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeekdaysAt(0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeekendsAt(0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOn(1, 0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnLastDay(0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.YearlyOn(1, 1, 0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.YearlyOn(new DateOnly(2024, 1, 1), 0, 0, second));
    }

    [Fact]
    public void Builder_DailyAt_InvalidTimeOnlyOrTimeSpan_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(new TimeOnly(9, 0, 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(TimeSpan.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void Builder_MonthlyOn_InvalidDayOfMonth_Throws(int dayOfMonth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOn(dayOfMonth, 0, 0));
    }

    [Fact]
    public void Builder_WeeklyOn_InvalidDayOfWeek_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeeklyOn((DayOfWeek)99, 0, 0));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(13, 1)]
    [InlineData(4, 31)]
    public void Builder_YearlyOn_InvalidMonthOrDay_Throws(int month, int dayOfMonth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.YearlyOn(month, dayOfMonth, 0, 0));
    }

    [Fact]
    public void Builder_InTimeZone_WithUnknownId_Throws()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0);

        Assert.Throws<TimeZoneNotFoundException>(() => builder.InTimeZone("Definitely/Not-A-TimeZone"));
    }
}
