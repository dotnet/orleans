#nullable enable
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronComplexPatternTests
{
    [Fact]
    public void Parse_FullSupportedGrammar_ComputesExpectedNextOccurrences()
    {
        var cases = new (string Expression, DateTime FromUtc, DateTime ExpectedUtc)[]
        {
            ("15 10 * * *", Utc(2026, 1, 1, 10, 14, 30), Utc(2026, 1, 1, 10, 15, 0)),
            ("10,20,30 9 * * *", Utc(2026, 1, 1, 9, 15, 0), Utc(2026, 1, 1, 9, 20, 0)),
            ("10-12 9 * * *", Utc(2026, 1, 1, 9, 10, 0), Utc(2026, 1, 1, 9, 11, 0)),
            ("*/15 * * * *", Utc(2026, 1, 1, 10, 7, 0), Utc(2026, 1, 1, 10, 15, 0)),
            ("10/20 * * * *", Utc(2026, 1, 1, 10, 15, 0), Utc(2026, 1, 1, 10, 30, 0)),
            ("5-15/5 * * * *", Utc(2026, 1, 1, 10, 6, 0), Utc(2026, 1, 1, 10, 10, 0)),
            ("0 3,5-11/3,12 1 * * *", Utc(2026, 1, 1, 1, 4, 0), Utc(2026, 1, 1, 1, 5, 0)),
            ("*/20 * * * * *", Utc(2026, 1, 1, 10, 0, 1), Utc(2026, 1, 1, 10, 0, 20)),
            ("0 9 * JAN,MAR MON-FRI", Utc(2026, 1, 2, 10, 0, 0), Utc(2026, 1, 5, 9, 0, 0)),
            ("0 9 ? * MON", Utc(2026, 1, 2, 10, 0, 0), Utc(2026, 1, 5, 9, 0, 0)),
            ("0 9 LW * *", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 1, 30, 9, 0, 0)),
            ("0 9 L-5W * *", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 1, 26, 9, 0, 0)),
            ("0 9 13 * FRI", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 2, 13, 9, 0, 0)),
            ("0 0 1 DEC-FEB *", Utc(2026, 12, 1, 0, 0, 0), Utc(2027, 1, 1, 0, 0, 0)),
            ("0 9 * * FRI-MON", Utc(2026, 1, 2, 10, 0, 0), Utc(2026, 1, 3, 9, 0, 0)),
            ("0 9 * * mon", Utc(2026, 1, 2, 10, 0, 0), Utc(2026, 1, 5, 9, 0, 0)),
            ("0 0 1-15/3 * *", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 1, 4, 0, 0, 0)),
            ("0 0 1 */3 *", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 4, 1, 0, 0, 0)),
            ("0 0 * * */2", Utc(2026, 1, 4, 0, 0, 0), Utc(2026, 1, 6, 0, 0, 0)),
            ("0 0 ? * 1/2", Utc(2026, 1, 5, 0, 0, 0), Utc(2026, 1, 7, 0, 0, 0)),
        };

        foreach (var (expressionText, fromUtc, expectedUtc) in cases)
        {
            var expression = ReminderCronExpression.Parse(expressionText);

            Assert.Equal(expectedUtc, expression.GetNextOccurrence(fromUtc));
        }

        static DateTime Utc(int year, int month, int day, int hour, int minute, int second)
            => new(year, month, day, hour, minute, second, DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("@weekly", 2026, 1, 18, 0, 0, 0)]
    [InlineData("@midnight", 2026, 1, 16, 0, 0, 0)]
    [InlineData("@every_second", 2026, 1, 15, 10, 0, 1)]
    [InlineData("@annually", 2027, 1, 1, 0, 0, 0)]
    public void Parse_AdditionalMacros_ComputeExpectedNextOccurrence(
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
    public void Parse_LastDayOffset_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 L-3 * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 28, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_LastNamedWeekday_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 ? * FRIL");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 30, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_NthNamedWeekday_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 ? * MON#2");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 12, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_NearestWeekday_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 1W 6 *");
        var fromUtc = new DateTime(2025, 5, 31, 23, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 6, 2, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_SecondsMonthListAndWeekdayRange_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("15 30 9 ? JAN,MAR MON-FRI");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 9, 30, 15, DateTimeKind.Utc), next);
    }
}
