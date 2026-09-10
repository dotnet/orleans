#nullable enable
using System.Globalization;

namespace Orleans.AdvancedReminders;

internal static class CronFieldExpressionFormatter
{
    public static CronFieldExpression Any() => new("*", false);

    public static CronFieldExpression Values(int[] values, int minimum, int maximum, string paramName)
    {
        ArgumentNullException.ThrowIfNull(values, paramName);
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", paramName);
        }

        var uniqueValues = new SortedSet<int>();
        foreach (var value in values)
        {
            Validate(value, minimum, maximum, paramName);
            uniqueValues.Add(value);
        }

        return new(FormatValues(uniqueValues), true);
    }

    public static CronFieldExpression Range(int start, int end, int minimum, int maximum)
    {
        Validate(start, minimum, maximum, nameof(start));
        Validate(end, minimum, maximum, nameof(end));
        return new($"{Format(start)}-{Format(end)}", true);
    }

    public static CronFieldExpression Step(int interval, int minimum, int maximum, bool sundayAlias = false)
    {
        ValidateInterval(interval, sundayAlias ? 7 : maximum - minimum + 1);
        return new($"*/{Format(interval)}", true);
    }

    public static CronFieldExpression Step(int start, int interval, int minimum, int maximum, bool sundayAlias = false)
    {
        Validate(start, minimum, maximum, nameof(start));
        ValidateInterval(interval, sundayAlias ? 7 : maximum - minimum + 1);
        return new($"{Format(start)}/{Format(interval)}", true);
    }

    public static CronFieldExpression Step(int start, int end, int interval, int minimum, int maximum, bool sundayAlias = false)
    {
        Validate(start, minimum, maximum, nameof(start));
        Validate(end, minimum, maximum, nameof(end));
        ValidateInterval(interval, sundayAlias ? 7 : maximum - minimum + 1);
        return new($"{Format(start)}-{Format(end)}/{Format(interval)}", true);
    }

    public static CronFieldExpression Special(string expression) => new(expression, false);

    public static CronFieldExpression SpecialValue(int value, string suffix, int minimum, int maximum, string paramName)
    {
        Validate(value, minimum, maximum, paramName);
        return Special($"{Format(value)}{suffix}");
    }

    public static CronFieldExpression Combine<T>(T[] parts, Func<T, CronFieldExpression> selector)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
        {
            throw new ArgumentException("At least one field part is required.", nameof(parts));
        }

        var expressions = new List<string>(parts.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            var expression = selector(part);
            if (!expression.CanCombine)
            {
                throw new ArgumentException("Wildcard and special cron fields cannot be used in a list.", nameof(parts));
            }

            if (seen.Add(expression.Text))
            {
                expressions.Add(expression.Text);
            }
        }

        return new(string.Join(",", expressions), true);
    }

    public static int ToCronDay(DayOfWeek dayOfWeek)
        => dayOfWeek switch
        {
            DayOfWeek.Sunday => 0,
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 3,
            DayOfWeek.Thursday => 4,
            DayOfWeek.Friday => 5,
            DayOfWeek.Saturday => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(dayOfWeek), dayOfWeek, null)
        };

    public static void Validate(int value, int minimum, int maximum, string paramName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be in [{minimum.ToString(CultureInfo.InvariantCulture)}, {maximum.ToString(CultureInfo.InvariantCulture)}].");
        }
    }

    private static void ValidateInterval(int interval, int maximum)
        => Validate(interval, 1, maximum, nameof(interval));

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatValues(IEnumerable<int> values)
    {
        var formatted = new List<string>();
        foreach (var value in values)
        {
            formatted.Add(Format(value));
        }

        return string.Join(",", formatted);
    }
}
