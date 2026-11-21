using System.Globalization;
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronExhaustiveRangeTests
{
    [Theory]
    [InlineData("second", 0, 59)]
    [InlineData("minute", 0, 59)]
    [InlineData("hour", 0, 23)]
    [InlineData("day", 1, 31)]
    [InlineData("month", 1, 12)]
    [InlineData("weekday", 0, 6)]
    public void EveryPairAndEveryStep_MatchesIndependentCalendar(string field, int minimum, int maximum)
    {
        var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var until = field switch
        {
            "second" => from.AddMinutes(2),
            "minute" => from.AddHours(2),
            "hour" => from.AddDays(2),
            "day" => from.AddMonths(2),
            "month" => from.AddYears(2),
            _ => from.AddDays(28),
        };
        var candidates = new List<(DateTime Utc, int Value)>();
        for (var cursor = from; cursor < until; cursor = Advance(cursor, field))
        {
            candidates.Add((cursor, Value(cursor, field)));
        }

        for (var first = minimum; first <= maximum; first++)
            for (var last = minimum; last <= maximum; last++)
                for (var step = 1; step <= maximum - minimum + 1; step++)
                {
                    // Walk the calendar positions, including the boundary, independently of parser bit masks.
                    var selected = new HashSet<int>();
                    var position = 0;
                    for (var value = first; ; value = value == maximum ? minimum : value + 1)
                    {
                        if (position++ % step == 0) selected.Add(value);
                        if (value == last) break;
                    }

                    var expected = candidates.Where(candidate => selected.Contains(candidate.Value)).Select(candidate => candidate.Utc).ToArray();
                    var range = FormattableString.Invariant($"{first}-{last}/{step}");
                    var text = Expression(field, range);
                    var actual = ReminderCronExpression.Parse(text).GetOccurrences(from, until).ToArray();
                    Assert.True(expected.SequenceEqual(actual), $"Wrong occurrences for {text} between {from:O} and {until:O}.");

                    if (step == 1)
                    {
                        var plainRange = Expression(field, FormattableString.Invariant($"{first}-{last}"));
                        Assert.Equal(expected, ReminderCronExpression.Parse(plainRange).GetOccurrences(from, until));
                    }

                    var typed = Typed(field, first, last, step).GetOccurrences(from, until).ToArray();
                    Assert.True(expected.SequenceEqual(typed), $"Wrong typed range for {text}.");
                }
    }

    [Theory]
    [InlineData("THU-WED", "0,1,2,3,4,5,6")]
    [InlineData("TUE-WED", "2,3")]
    [InlineData("WED-TUE", "0,1,2,3,4,5,6")]
    [InlineData("FRI-MON", "0,1,5,6")]
    [InlineData("SAT-SUN", "0,6")]
    [InlineData("SUN-SUN", "0")]
    [InlineData("MON-MON", "1")]
    [InlineData("THU-WED/2", "1,3,4,6")]
    [InlineData("FRI-MON/2", "0,5")]
    [InlineData("TUE-WED/2", "2")]
    [InlineData("MON-WED,TUE-THU", "1,2,3,4")]
    [InlineData("0-7", "0,1,2,3,4,5,6")]
    [InlineData("6-7", "0,6")]
    [InlineData("7-2", "0,1,2")]
    [InlineData("7-7", "0")]
    [InlineData("5-7/2", "0,5")]
    [InlineData("thu-wed", "0,1,2,3,4,5,6")]
    public void NamedRangesAliasesAndOverlappingLists_HaveExplicitExpectedDays(string range, string days)
    {
        var selected = days.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToHashSet();
        var from = new DateTime(2025, 12, 20, 9, 30, 0, DateTimeKind.Utc);
        var until = from.AddDays(35);
        var expected = Enumerable.Range(0, 35).Select(index => from.AddDays(index)).Where(day => selected.Contains((int)day.DayOfWeek));
        var actual = ReminderCronExpression.Parse($"30 9 * * {range}").GetOccurrences(from, until);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryNamedPairAndStep_MatchesCalendarIncludingCaseVariants(bool months)
    {
        string[] names = months
            ? ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]
            : ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
        var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var until = months ? from.AddYears(2) : from.AddDays(28);
        for (var first = 0; first < names.Length; first++)
            for (var last = 0; last < names.Length; last++)
                for (var step = 1; step <= names.Length; step++)
                {
                    var selected = new HashSet<int>();
                    var position = 0;
                    for (var day = first; ; day = day + 1 == names.Length ? 0 : day + 1)
                    {
                        if (position++ % step == 0) selected.Add(day);
                        if (day == last) break;
                    }

                    var field = $"{names[first].ToLowerInvariant()}-{names[last]}/{step.ToString(CultureInfo.InvariantCulture)}";
                    var expected = new List<DateTime>();
                    for (var date = from; date < until; date = months ? date.AddMonths(1) : date.AddDays(1))
                    {
                        if (selected.Contains(months ? date.Month - 1 : (int)date.DayOfWeek)) expected.Add(date);
                    }

                    Assert.Equal(expected, ReminderCronExpression.Parse(Expression(months ? "month" : "weekday", field)).GetOccurrences(from, until));
                }
    }

    [Fact]
    public void EveryNumericWeekdayAliasPairAndStep_MatchesTheSameSevenDayCalendar()
    {
        var from = new DateTime(2025, 12, 20, 0, 0, 0, DateTimeKind.Utc);
        for (var first = 0; first <= 7; first++)
            for (var last = 0; last <= 7; last++)
                for (var step = 1; step <= 7; step++)
                {
                    var end = last;
                    while (end < first) end += 7;
                    var selected = new HashSet<int>();
                    for (var day = first; day <= end; day += step) selected.Add(day % 7);
                    var expected = Enumerable.Range(0, 28).Select(index => from.AddDays(index)).Where(date => selected.Contains((int)date.DayOfWeek));
                    var text = FormattableString.Invariant($"0 0 * * {first}-{last}/{step}");
                    Assert.Equal(expected, ReminderCronExpression.Parse(text).GetOccurrences(from, from.AddDays(28)));
                }
    }

    private static ReminderCronBuilder Typed(string field, int first, int last, int step)
        => ReminderCronBuilder.FromFields(
            field == "second" ? ReminderCronSecond.EveryBetween(first, last, step) : ReminderCronSecond.At(0),
            field == "minute" ? ReminderCronMinute.EveryBetween(first, last, step) : field == "second" ? ReminderCronMinute.Any : ReminderCronMinute.At(0),
            field == "hour" ? ReminderCronHour.EveryBetween(first, last, step) : field is "second" or "minute" ? ReminderCronHour.Any : ReminderCronHour.At(0),
            field == "day" ? ReminderCronDayOfMonth.EveryBetween(first, last, step) : field == "month" ? ReminderCronDayOfMonth.On(1) : ReminderCronDayOfMonth.Any,
            field == "month" ? ReminderCronMonth.EveryBetween(first, last, step) : ReminderCronMonth.Any,
            field == "weekday" ? ReminderCronDayOfWeek.EveryBetween((DayOfWeek)first, (DayOfWeek)last, step) : ReminderCronDayOfWeek.Any);

    private static string Expression(string field, string range) => field switch
    {
        "second" => $"{range} * * * * *",
        "minute" => $"{range} * * * *",
        "hour" => $"0 {range} * * *",
        "day" => $"0 0 {range} * *",
        "month" => $"0 0 1 {range} *",
        _ => $"0 0 * * {range}",
    };

    private static DateTime Advance(DateTime value, string field) => field switch
    {
        "second" => value.AddSeconds(1),
        "minute" => value.AddMinutes(1),
        "hour" => value.AddHours(1),
        "month" => value.AddMonths(1),
        _ => value.AddDays(1),
    };

    private static int Value(DateTime value, string field) => field switch
    {
        "second" => value.Second,
        "minute" => value.Minute,
        "hour" => value.Hour,
        "day" => value.Day,
        "month" => value.Month,
        _ => (int)value.DayOfWeek,
    };
}
