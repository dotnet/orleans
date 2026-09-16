using System.Globalization;
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronGregorianCycleTests
{
    public static IEnumerable<object[]> CalendarCases()
    {
        foreach (var day in new[] { 1, 15, 28, 29, 30, 31 })
        {
            yield return ["nearest", day, 0, $"{day.ToString(CultureInfo.InvariantCulture)}W", "*"];
        }

        foreach (var offset in new[] { 0, 1, 3, 27, 28, 29, 30 })
        {
            var field = offset == 0 ? "L" : $"L-{offset.ToString(CultureInfo.InvariantCulture)}";
            yield return ["last", offset, 0, field, "*"];
            yield return ["last-nearest", offset, 0, field + "W", "*"];
        }

        for (var weekday = 0; weekday < 7; weekday++)
        {
            var field = weekday.ToString(CultureInfo.InvariantCulture);
            yield return ["last-weekday", weekday, 0, "?", field + "L"];
            yield return ["and", weekday, 13, "13", field];
            for (var occurrence = 1; occurrence <= 5; occurrence++)
            {
                yield return ["nth", weekday, occurrence, "?", $"{field}#{occurrence.ToString(CultureInfo.InvariantCulture)}"];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CalendarCases))]
    public void CalendarSelectors_MatchDateTimeOracleAcrossFourHundredYears(string rule, int argument, int ordinal, string monthDay, string weekDay)
    {
        var expression = ReminderCronExpression.Parse($"23 17 9 {monthDay} * {weekDay}");
        var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var until = from.AddYears(400);
        var expected = new List<DateTime>();
        for (var month = from; month < until; month = month.AddMonths(1))
        {
            var dates = Enumerable.Range(0, DateTime.DaysInMonth(month.Year, month.Month))
                .Select(offset => month.AddDays(offset)).ToArray();
            IEnumerable<DateTime> matches = rule switch
            {
                "last" => dates.AsEnumerable().Reverse().Skip(argument).Take(1),
                "nearest" => NearestWeekday(dates, dates.Where(date => date.Day == argument).ToArray()),
                "last-nearest" => NearestWeekday(dates, dates.AsEnumerable().Reverse().Skip(argument).Take(1).ToArray()),
                "last-weekday" => dates.Where(date => (int)date.DayOfWeek == argument).TakeLast(1),
                "nth" => dates.Where(date => (int)date.DayOfWeek == argument).Skip(ordinal - 1).Take(1),
                "and" => dates.Where(date => date.Day == ordinal && (int)date.DayOfWeek == argument),
                _ => throw new InvalidOperationException(rule),
            };
            expected.AddRange(matches.Select(date => date.AddHours(9).AddMinutes(17).AddSeconds(23)));
        }

        Assert.Equal(expected, expression.GetOccurrences(from, until));
    }

    private static IEnumerable<DateTime> NearestWeekday(DateTime[] dates, DateTime[] target)
        => target.Length == 0 ? [] : dates
            .Where(date => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .OrderBy(date => Math.Abs((date - target[0]).Days))
            .Take(1);
}
