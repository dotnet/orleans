using System.Globalization;
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronGrammarPropertyTests
{
    [Fact]
    public void GeneratedRangesListsAndSteps_MatchIndependentUtcScan()
    {
        var random = new Random(882_917);
        for (var sample = 0; sample < 400; sample++)
        {
            var second = GenerateField(random, 60);
            var minute = GenerateField(random, 60);
            var hour = GenerateField(random, 24);
            var text = $"{second.Text} {minute.Text} {hour.Text} * * *";
            var expression = ReminderCronExpression.Parse(text);
            var from = DateTime.UnixEpoch.AddDays(sample).AddSeconds(random.Next(86_400)).AddTicks(sample % 7);
            var inclusive = sample % 2 == 0;
            DateTime? expected = null;
            var cursor = new DateTime(from.Ticks - from.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            for (var step = 0; step <= 86_400; step++, cursor = cursor.AddSeconds(1))
            {
                if ((cursor > from || inclusive && cursor == from)
                    && hour.Values.Contains(cursor.Hour) && minute.Values.Contains(cursor.Minute) && second.Values.Contains(cursor.Second))
                {
                    expected = cursor;
                    break;
                }
            }

            Assert.NotNull(expected);
            Assert.Equal(expected, expression.GetNextOccurrence(from, inclusive));
        }
    }

    [Fact]
    public void ArbitraryMalformedInput_TryParseNeverLeaksUnexpectedExceptions()
    {
        const string alphabet = "0123456789*/,-?#LW@JANMOfx \t\r\n\0";
        var random = new Random(994_313);
        for (var sample = 0; sample < 5000; sample++)
        {
            var input = new string(Enumerable.Range(0, random.Next(0, 128)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var valid = ReminderCronExpression.TryParse(input, out var parsed);
            Assert.Equal(valid, parsed is not null);
        }
    }

    [Theory]
    [InlineData("1,,2 * * * *")]
    [InlineData("1, * * * *")]
    [InlineData(",1 * * * *")]
    [InlineData("1//2 * * * *")]
    [InlineData("1-2-3 * * * *")]
    [InlineData("*/999999999999999999999 * * * *")]
    [InlineData("-1 * * * *")]
    [InlineData("+1 * * * *")]
    [InlineData("١ * * * *")]
    [InlineData("? * * * *")]
    [InlineData("0 0 ? ? *")]
    [InlineData("0 0 L-31 * *")]
    [InlineData("0 0 32W * *")]
    [InlineData("0 0 * * MON#1#2")]
    [InlineData("0 0 * * L")]
    [InlineData("0 0 * * 1L,2L")]
    public void MalformedTokens_AreRejected(string text)
    {
        Assert.False(ReminderCronExpression.TryParse(text, out _));
        Assert.ThrowsAny<FormatException>(() => ReminderCronExpression.Parse(text));
    }

    private static (string Text, HashSet<int> Values) GenerateField(Random random, int size)
    {
        var first = random.Next(size);
        var last = random.Next(size);
        var step = random.Next(1, size + 1);
        var mode = random.Next(6);
        string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        if (mode == 0) return ("*", Enumerable.Range(0, size).ToHashSet());
        if (mode == 1) return (Number(first), [first]);
        if (mode == 2) return ($"{Number(first)},{Number(last)}", [first, last]);
        if (mode == 3) return ($"{Number(first)}/{Number(step)}", Enumerable.Range(first, size - first).Where(value => (value - first) % step == 0).ToHashSet());

        var sequence = new List<int>();
        for (var value = first; ; value = value + 1 == size ? 0 : value + 1)
        {
            sequence.Add(value);
            if (value == last) break;
        }

        var field = $"{Number(first)}-{Number(last)}";
        return mode == 4
            ? (field, sequence.ToHashSet())
            : ($"{field}/{Number(step)}", sequence.Where((_, index) => index % step == 0).ToHashSet());
    }
}
