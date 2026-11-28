using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronRangeValidationTests
{
    [Theory]
    [InlineData("THU-")]
    [InlineData("-WED")]
    [InlineData("THU--WED")]
    [InlineData("THU-WED-MON")]
    [InlineData("THUR-WED")]
    [InlineData("THU-WEDNESDAY")]
    [InlineData("THU-WED/0")]
    [InlineData("THU-WED/-1")]
    [InlineData("THU-WED/8")]
    [InlineData("THU-WED/9")]
    [InlineData("THU-WED/2147483648")]
    [InlineData("THU-WED/")]
    [InlineData("THU-WED/2/3")]
    [InlineData("THU-WED,,TUE")]
    [InlineData("THU-WED,")]
    [InlineData(",TUE-WED")]
    [InlineData("THU-8")]
    [InlineData("8-WED")]
    [InlineData("THU-WED#2")]
    [InlineData("THU-WEDL")]
    [InlineData("THU-WEDW")]
    [InlineData("THU-?")]
    public void MalformedWeekdayRanges_AreRejectedByBothEntryPoints(string range)
    {
        var text = $"30 9 * * {range}";
        Assert.False(ReminderCronExpression.TryParse(text, out var parsed));
        Assert.Null(parsed);
        Assert.ThrowsAny<FormatException>(() => ReminderCronExpression.Parse(text));
    }

    [Theory]
    [InlineData(0, 59, "{0} * * * * *")]
    [InlineData(0, 59, "{0} * * * *")]
    [InlineData(0, 23, "0 {0} * * *")]
    [InlineData(1, 31, "0 0 {0} * *")]
    [InlineData(1, 12, "0 0 1 {0} *")]
    [InlineData(0, 7, "0 0 * * {0}")]
    public void EveryValidEndpoint_RejectsInvalidOppositeEndpointAndStep(int minimum, int maximum, string template)
    {
        for (var endpoint = minimum; endpoint <= maximum; endpoint++)
            foreach (var invalid in new[] { int.MinValue, -1, minimum - 1, maximum + 1, int.MaxValue })
                foreach (var token in new[] { $"{endpoint}-{invalid}", $"{invalid}-{endpoint}" })
                {
                    Reject(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, token));
                }

        for (var first = minimum; first <= maximum; first++)
            for (var last = minimum; last <= maximum; last++)
                foreach (var invalidStep in new[] { 0, -1, maximum - minimum + 2, int.MaxValue })
                {
                    Reject(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, $"{first}-{last}/{invalidStep}"));
                }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void TypedWeekdayRanges_RejectUndefinedEnumsAtEitherEnd(int invalid)
    {
        foreach (var valid in Enum.GetValues<DayOfWeek>())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.Range((DayOfWeek)invalid, valid));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.Range(valid, (DayOfWeek)invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.EveryBetween((DayOfWeek)invalid, valid, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.EveryBetween(valid, (DayOfWeek)invalid, 2));
        }
    }

    [Fact]
    public void FullFieldSteps_AreAcceptedWithoutAllowingAnEighthWeekday()
    {
        var date = DateTime.UnixEpoch;
        ReminderCronBuilder Build(ReminderCronSecond second, ReminderCronMinute minute, ReminderCronHour hour)
            => ReminderCronBuilder.FromFields(second, minute, hour, ReminderCronDayOfMonth.Any, ReminderCronMonth.Any, ReminderCronDayOfWeek.Any);

        Assert.Equal(date.AddMinutes(1), Build(ReminderCronSecond.Every(60), ReminderCronMinute.Any, ReminderCronHour.Any).GetNextOccurrence(date));
        Assert.Equal(date.AddSeconds(59), Build(ReminderCronSecond.EveryFrom(59, 60), ReminderCronMinute.Any, ReminderCronHour.Any).GetNextOccurrence(date));
        Assert.Equal(date.AddHours(1), Build(ReminderCronSecond.At(0), ReminderCronMinute.Every(60), ReminderCronHour.Any).GetNextOccurrence(date));
        Assert.Equal(date.AddMinutes(59), Build(ReminderCronSecond.At(0), ReminderCronMinute.EveryFrom(59, 60), ReminderCronHour.Any).GetNextOccurrence(date));
        Assert.Equal(date.AddDays(1), Build(ReminderCronSecond.At(0), ReminderCronMinute.At(0), ReminderCronHour.Every(24)).GetNextOccurrence(date));
        Assert.Equal(date.AddHours(23), Build(ReminderCronSecond.At(0), ReminderCronMinute.At(0), ReminderCronHour.EveryFrom(23, 24)).GetNextOccurrence(date));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.Every(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.EveryFrom(DayOfWeek.Tuesday, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.EveryBetween(DayOfWeek.Tuesday, DayOfWeek.Friday, 8));
        Reject("0 0 * * */8");
    }

    private static void Reject(string expression)
    {
        Assert.False(ReminderCronExpression.TryParse(expression, out var parsed), expression);
        Assert.Null(parsed);
        Assert.ThrowsAny<FormatException>(() => ReminderCronExpression.Parse(expression));
    }
}
