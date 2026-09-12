using System.Globalization;
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronExpressionFuzzTests
{
    [Fact]
    public void GeneratedClockSets_MatchIndependentSecondBySecondSearch()
    {
        var random = new Random(138_931);
        for (var sample = 0; sample < 120; sample++)
        {
            var seconds = SelectValues(60);
            var minutes = SelectValues(60);
            var hours = SelectValues(24);
            var expression = ReminderCronExpression.Parse($"{Format(seconds)} {Format(minutes)} {Format(hours)} * * *");
            var from = new DateTime(2024 + sample % 4, 1 + sample % 12, 1 + sample % 27,
                random.Next(24), random.Next(60), random.Next(60), DateTimeKind.Utc).AddTicks(sample % 3);
            var inclusive = sample % 2 == 0;
            DateTime? expected = null;
            var cursor = new DateTime(from.Ticks - from.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            for (var step = 0; step <= 86_400; step++, cursor = cursor.AddSeconds(1))
            {
                if ((cursor > from || inclusive && cursor == from)
                    && hours.Contains(cursor.Hour) && minutes.Contains(cursor.Minute) && seconds.Contains(cursor.Second))
                {
                    expected = cursor;
                    break;
                }
            }

            Assert.NotNull(expected);
            Assert.Equal(expected, expression.GetNextOccurrence(from, inclusive));
        }

        int[] SelectValues(int count) => Enumerable.Range(0, count).Where(_ => random.Next(5) == 0).Append(random.Next(count)).Distinct().ToArray();
        static string Format(int[] values) => string.Join(',', values.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }
}
