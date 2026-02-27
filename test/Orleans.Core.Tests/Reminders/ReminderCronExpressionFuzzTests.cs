using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Orleans;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderCronExpressionFuzzTests
{
    [Fact]
    public void Fuzz_InternalCanonicalRoundTrip_PreservesSchedule()
    {
        var random = new Random(138_931);

        for (var i = 0; i < 300; i++)
        {
            var expressionText =
                $"{GenerateTimeField(random, 0, 59)} {GenerateTimeField(random, 0, 59)} {GenerateTimeField(random, 0, 23)} * * ?";

            var original = ReminderCronExpression.Parse(expressionText);
            var canonicalText = GetInternalCronExpressionText(original);
            var canonical = ReminderCronExpression.Parse(canonicalText);

            for (var j = 0; j < 10; j++)
            {
                var fromUtc = GenerateUtcInstant(random);
                var inclusive = random.Next(2) == 0;

                var expected = original.GetNextOccurrence(fromUtc, inclusive);
                var actual = canonical.GetNextOccurrence(fromUtc, inclusive);

                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Fuzz_GetOccurrences_AreStrictlyIncreasingAndMatchFirstNextOccurrence()
    {
        var random = new Random(812_377);

        for (var i = 0; i < 250; i++)
        {
            var expressionText =
                $"{GenerateTimeField(random, 0, 59)} {GenerateTimeField(random, 0, 59)} {GenerateTimeField(random, 0, 23)} * * ?";

            var expression = ReminderCronExpression.Parse(expressionText);
            var fromUtc = GenerateUtcInstant(random);
            var toUtc = fromUtc.AddDays(random.Next(1, 5));

            var occurrences = expression.GetOccurrences(fromUtc, toUtc).ToArray();

            for (var index = 0; index < occurrences.Length; index++)
            {
                var occurrence = occurrences[index];
                Assert.True(occurrence >= fromUtc, $"Occurrence {occurrence:o} was earlier than from {fromUtc:o}. Expression: {expressionText}");
                Assert.True(occurrence < toUtc, $"Occurrence {occurrence:o} was not before to {toUtc:o}. Expression: {expressionText}");
                if (index > 0)
                {
                    Assert.True(
                        occurrence > occurrences[index - 1],
                        $"Occurrences were not strictly increasing: {occurrences[index - 1]:o} then {occurrence:o}. Expression: {expressionText}");
                }
            }

            var next = expression.GetNextOccurrence(fromUtc, inclusive: true);
            if (next is { } first && first < toUtc)
            {
                Assert.NotEmpty(occurrences);
                Assert.Equal(first, occurrences[0]);
            }
            else
            {
                Assert.Empty(occurrences);
            }
        }
    }

    private static string GenerateTimeField(Random random, int min, int max)
    {
        var mode = random.Next(6);
        return mode switch
        {
            0 => "*",
            1 => random.Next(min, max + 1).ToString(CultureInfo.InvariantCulture),
            2 => GenerateListField(random, min, max),
            3 => GenerateRangeField(random, min, max),
            4 => $"*/{random.Next(1, Math.Min(max - min + 1, 12) + 1).ToString(CultureInfo.InvariantCulture)}",
            _ => GenerateSteppedRangeField(random, min, max)
        };
    }

    private static string GenerateListField(Random random, int min, int max)
    {
        var count = random.Next(2, 7);
        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            // Keep duplicates and non-sorted order intentionally: parser normalizes into bitsets.
            values[i] = random.Next(min, max + 1);
        }

        return string.Join(",", values.Select(static v => v.ToString(CultureInfo.InvariantCulture)));
    }

    private static string GenerateRangeField(Random random, int min, int max)
    {
        var left = random.Next(min, max + 1);
        var right = random.Next(min, max + 1);
        return $"{left.ToString(CultureInfo.InvariantCulture)}-{right.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string GenerateSteppedRangeField(Random random, int min, int max)
    {
        var left = random.Next(min, max + 1);
        var right = random.Next(min, max + 1);
        var step = random.Next(1, Math.Min(max - min + 1, 12) + 1);
        return $"{left.ToString(CultureInfo.InvariantCulture)}-{right.ToString(CultureInfo.InvariantCulture)}/{step.ToString(CultureInfo.InvariantCulture)}";
    }

    private static DateTime GenerateUtcInstant(Random random)
    {
        var year = random.Next(2024, 2028);
        var month = random.Next(1, 13);
        var day = random.Next(1, DateTime.DaysInMonth(year, month) + 1);
        var hour = random.Next(0, 24);
        var minute = random.Next(0, 60);
        var second = random.Next(0, 60);

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
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
