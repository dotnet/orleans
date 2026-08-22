#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Defines the seconds field of a typed reminder cron expression.
/// </summary>
public sealed class ReminderCronSecond
{
    private ReminderCronSecond(CronFieldExpression expression) => Expression = expression;

    internal CronFieldExpression Expression { get; }

    /// <summary>Matches every second.</summary>
    public static ReminderCronSecond Any { get; } = new(CronFieldExpressionFormatter.Any());

    /// <summary>Matches the selected seconds.</summary>
    public static ReminderCronSecond At(params int[] seconds)
        => new(CronFieldExpressionFormatter.Values(seconds, 0, 59, nameof(seconds)));

    /// <summary>Matches an inclusive range. A reversed range wraps across the field boundary.</summary>
    public static ReminderCronSecond Range(int start, int end)
        => new(CronFieldExpressionFormatter.Range(start, end, 0, 59));

    /// <summary>Matches every <paramref name="interval"/> seconds, starting at zero.</summary>
    public static ReminderCronSecond Every(int interval)
        => new(CronFieldExpressionFormatter.Step(interval, 0, 59));

    /// <summary>Matches every <paramref name="interval"/> seconds from <paramref name="start"/> through 59.</summary>
    public static ReminderCronSecond EveryFrom(int start, int interval)
        => new(CronFieldExpressionFormatter.Step(start, interval, 0, 59));

    /// <summary>Matches every <paramref name="interval"/> seconds within an inclusive range.</summary>
    public static ReminderCronSecond EveryBetween(int start, int end, int interval)
        => new(CronFieldExpressionFormatter.Step(start, end, interval, 0, 59));

    /// <summary>Combines values, ranges, and steps using cron list semantics.</summary>
    public static ReminderCronSecond Combine(params ReminderCronSecond[] parts)
        => new(CronFieldExpressionFormatter.Combine(parts, static part => part.Expression));
}

/// <summary>
/// Defines the minutes field of a typed reminder cron expression.
/// </summary>
public sealed class ReminderCronMinute
{
    private ReminderCronMinute(CronFieldExpression expression) => Expression = expression;

    internal CronFieldExpression Expression { get; }

    /// <summary>Matches every minute.</summary>
    public static ReminderCronMinute Any { get; } = new(CronFieldExpressionFormatter.Any());

    /// <summary>Matches the selected minutes.</summary>
    public static ReminderCronMinute At(params int[] minutes)
        => new(CronFieldExpressionFormatter.Values(minutes, 0, 59, nameof(minutes)));

    /// <summary>Matches an inclusive range. A reversed range wraps across the field boundary.</summary>
    public static ReminderCronMinute Range(int start, int end)
        => new(CronFieldExpressionFormatter.Range(start, end, 0, 59));

    /// <summary>Matches every <paramref name="interval"/> minutes, starting at zero.</summary>
    public static ReminderCronMinute Every(int interval)
        => new(CronFieldExpressionFormatter.Step(interval, 0, 59));

    /// <summary>Matches every <paramref name="interval"/> minutes from <paramref name="start"/> through 59.</summary>
    public static ReminderCronMinute EveryFrom(int start, int interval)
        => new(CronFieldExpressionFormatter.Step(start, interval, 0, 59));

    /// <summary>Matches every <paramref name="interval"/> minutes within an inclusive range.</summary>
    public static ReminderCronMinute EveryBetween(int start, int end, int interval)
        => new(CronFieldExpressionFormatter.Step(start, end, interval, 0, 59));

    /// <summary>Combines values, ranges, and steps using cron list semantics.</summary>
    public static ReminderCronMinute Combine(params ReminderCronMinute[] parts)
        => new(CronFieldExpressionFormatter.Combine(parts, static part => part.Expression));
}

/// <summary>
/// Defines the hours field of a typed reminder cron expression.
/// </summary>
public sealed class ReminderCronHour
{
    private ReminderCronHour(CronFieldExpression expression) => Expression = expression;

    internal CronFieldExpression Expression { get; }

    /// <summary>Matches every hour.</summary>
    public static ReminderCronHour Any { get; } = new(CronFieldExpressionFormatter.Any());

    /// <summary>Matches the selected hours.</summary>
    public static ReminderCronHour At(params int[] hours)
        => new(CronFieldExpressionFormatter.Values(hours, 0, 23, nameof(hours)));

    /// <summary>Matches an inclusive range. A reversed range wraps across the field boundary.</summary>
    public static ReminderCronHour Range(int start, int end)
        => new(CronFieldExpressionFormatter.Range(start, end, 0, 23));

    /// <summary>Matches every <paramref name="interval"/> hours, starting at zero.</summary>
    public static ReminderCronHour Every(int interval)
        => new(CronFieldExpressionFormatter.Step(interval, 0, 23));

    /// <summary>Matches every <paramref name="interval"/> hours from <paramref name="start"/> through 23.</summary>
    public static ReminderCronHour EveryFrom(int start, int interval)
        => new(CronFieldExpressionFormatter.Step(start, interval, 0, 23));

    /// <summary>Matches every <paramref name="interval"/> hours within an inclusive range.</summary>
    public static ReminderCronHour EveryBetween(int start, int end, int interval)
        => new(CronFieldExpressionFormatter.Step(start, end, interval, 0, 23));

    /// <summary>Combines values, ranges, and steps using cron list semantics.</summary>
    public static ReminderCronHour Combine(params ReminderCronHour[] parts)
        => new(CronFieldExpressionFormatter.Combine(parts, static part => part.Expression));
}

/// <summary>
/// Defines the day-of-month field of a typed reminder cron expression.
/// </summary>
public sealed class ReminderCronDayOfMonth
{
    private ReminderCronDayOfMonth(CronFieldExpression expression) => Expression = expression;

    internal CronFieldExpression Expression { get; }

    /// <summary>Matches every day of the month.</summary>
    public static ReminderCronDayOfMonth Any { get; } = new(CronFieldExpressionFormatter.Any());

    /// <summary>Matches the selected days of the month.</summary>
    public static ReminderCronDayOfMonth On(params int[] days)
        => new(CronFieldExpressionFormatter.Values(days, 1, 31, nameof(days)));

    /// <summary>Matches an inclusive range. A reversed range wraps across the field boundary.</summary>
    public static ReminderCronDayOfMonth Range(int start, int end)
        => new(CronFieldExpressionFormatter.Range(start, end, 1, 31));

    /// <summary>Matches every <paramref name="interval"/> calendar positions in the field, starting at day 1.</summary>
    public static ReminderCronDayOfMonth Every(int interval)
        => new(CronFieldExpressionFormatter.Step(interval, 1, 31));

    /// <summary>Matches every <paramref name="interval"/> calendar positions from <paramref name="start"/> through day 31.</summary>
    public static ReminderCronDayOfMonth EveryFrom(int start, int interval)
        => new(CronFieldExpressionFormatter.Step(start, interval, 1, 31));

    /// <summary>Matches every <paramref name="interval"/> calendar positions within an inclusive range.</summary>
    public static ReminderCronDayOfMonth EveryBetween(int start, int end, int interval)
        => new(CronFieldExpressionFormatter.Step(start, end, interval, 1, 31));

    /// <summary>Matches the weekday nearest to the selected day without crossing a month boundary.</summary>
    public static ReminderCronDayOfMonth NearestWeekday(int day)
        => new(CronFieldExpressionFormatter.SpecialValue(day, "W", 1, 31, nameof(day)));

    /// <summary>Matches the last day of each month.</summary>
    public static ReminderCronDayOfMonth LastDay { get; } = new(CronFieldExpressionFormatter.Special("L"));

    /// <summary>Matches a fixed number of days before the last day of each month.</summary>
    public static ReminderCronDayOfMonth DaysBeforeLast(int offset)
    {
        CronFieldExpressionFormatter.Validate(offset, 1, 30, nameof(offset));
        return new(CronFieldExpressionFormatter.Special($"L-{offset.ToString(CultureInfo.InvariantCulture)}"));
    }

    /// <summary>Matches the last weekday of each month.</summary>
    public static ReminderCronDayOfMonth LastWeekday { get; } = new(CronFieldExpressionFormatter.Special("LW"));

    /// <summary>Matches the weekday nearest to a fixed offset before the last day of each month.</summary>
    public static ReminderCronDayOfMonth NearestWeekdayBeforeLast(int offset)
    {
        CronFieldExpressionFormatter.Validate(offset, 1, 30, nameof(offset));
        return new(CronFieldExpressionFormatter.Special($"L-{offset.ToString(CultureInfo.InvariantCulture)}W"));
    }

    /// <summary>Combines ordinary values, ranges, and steps using cron list semantics.</summary>
    public static ReminderCronDayOfMonth Combine(params ReminderCronDayOfMonth[] parts)
        => new(CronFieldExpressionFormatter.Combine(parts, static part => part.Expression));
}

/// <summary>
/// Defines the month field of a typed reminder cron expression.
/// </summary>
public sealed class ReminderCronMonth
{
    private ReminderCronMonth(CronFieldExpression expression) => Expression = expression;

    internal CronFieldExpression Expression { get; }

    /// <summary>Matches every month.</summary>
    public static ReminderCronMonth Any { get; } = new(CronFieldExpressionFormatter.Any());

    /// <summary>Matches the selected months, where January is 1 and December is 12.</summary>
    public static ReminderCronMonth In(params int[] months)
        => new(CronFieldExpressionFormatter.Values(months, 1, 12, nameof(months)));

    /// <summary>Matches an inclusive range. A reversed range wraps across the year boundary.</summary>
    public static ReminderCronMonth Range(int start, int end)
        => new(CronFieldExpressionFormatter.Range(start, end, 1, 12));

    /// <summary>Matches every <paramref name="interval"/> calendar positions in the field, starting in January.</summary>
    public static ReminderCronMonth Every(int interval)
        => new(CronFieldExpressionFormatter.Step(interval, 1, 12));

    /// <summary>Matches every <paramref name="interval"/> calendar positions from <paramref name="start"/> through December.</summary>
    public static ReminderCronMonth EveryFrom(int start, int interval)
        => new(CronFieldExpressionFormatter.Step(start, interval, 1, 12));

    /// <summary>Matches every <paramref name="interval"/> calendar positions within an inclusive range.</summary>
    public static ReminderCronMonth EveryBetween(int start, int end, int interval)
        => new(CronFieldExpressionFormatter.Step(start, end, interval, 1, 12));

    /// <summary>Combines values, ranges, and steps using cron list semantics.</summary>
    public static ReminderCronMonth Combine(params ReminderCronMonth[] parts)
        => new(CronFieldExpressionFormatter.Combine(parts, static part => part.Expression));
}

/// <summary>
/// Defines the day-of-week field of a typed reminder cron expression.
/// </summary>
public sealed class ReminderCronDayOfWeek
{
    private ReminderCronDayOfWeek(CronFieldExpression expression) => Expression = expression;

    internal CronFieldExpression Expression { get; }

    /// <summary>Matches every day of the week.</summary>
    public static ReminderCronDayOfWeek Any { get; } = new(CronFieldExpressionFormatter.Any());

    /// <summary>Matches the selected days of the week.</summary>
    public static ReminderCronDayOfWeek On(params DayOfWeek[] days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var values = new int[days.Length];
        for (var i = 0; i < days.Length; i++)
        {
            values[i] = CronFieldExpressionFormatter.ToCronDay(days[i]);
        }

        return new(CronFieldExpressionFormatter.Values(values, 0, 6, nameof(days)));
    }

    /// <summary>Matches an inclusive range. A reversed range wraps across the week boundary.</summary>
    public static ReminderCronDayOfWeek Range(DayOfWeek start, DayOfWeek end)
        => new(CronFieldExpressionFormatter.Range(
            CronFieldExpressionFormatter.ToCronDay(start),
            CronFieldExpressionFormatter.ToCronDay(end),
            0,
            6));

    /// <summary>Matches every <paramref name="interval"/> calendar positions in the field, starting on Sunday.</summary>
    public static ReminderCronDayOfWeek Every(int interval)
        => new(CronFieldExpressionFormatter.Step(interval, 0, 7));

    /// <summary>Matches every <paramref name="interval"/> calendar positions from <paramref name="start"/> through Sunday.</summary>
    public static ReminderCronDayOfWeek EveryFrom(DayOfWeek start, int interval)
        => new(CronFieldExpressionFormatter.Step(CronFieldExpressionFormatter.ToCronDay(start), interval, 0, 7));

    /// <summary>Matches every <paramref name="interval"/> calendar positions within an inclusive range.</summary>
    public static ReminderCronDayOfWeek EveryBetween(DayOfWeek start, DayOfWeek end, int interval)
        => new(CronFieldExpressionFormatter.Step(
            CronFieldExpressionFormatter.ToCronDay(start),
            CronFieldExpressionFormatter.ToCronDay(end),
            interval,
            0,
            7));

    /// <summary>Matches the last occurrence of the selected weekday in each month.</summary>
    public static ReminderCronDayOfWeek Last(DayOfWeek day)
        => new(CronFieldExpressionFormatter.SpecialValue(CronFieldExpressionFormatter.ToCronDay(day), "L", 0, 6, nameof(day)));

    /// <summary>Matches a selected occurrence of the weekday in each month.</summary>
    public static ReminderCronDayOfWeek Nth(DayOfWeek day, int occurrence)
    {
        CronFieldExpressionFormatter.Validate(occurrence, 1, 5, nameof(occurrence));
        return new(CronFieldExpressionFormatter.Special(
            $"{CronFieldExpressionFormatter.ToCronDay(day).ToString(CultureInfo.InvariantCulture)}#{occurrence.ToString(CultureInfo.InvariantCulture)}"));
    }

    /// <summary>Combines ordinary values, ranges, and steps using cron list semantics.</summary>
    public static ReminderCronDayOfWeek Combine(params ReminderCronDayOfWeek[] parts)
        => new(CronFieldExpressionFormatter.Combine(parts, static part => part.Expression));
}

internal readonly record struct CronFieldExpression(string Text, bool CanCombine);

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

    public static CronFieldExpression Step(int interval, int minimum, int maximum)
    {
        ValidateInterval(interval, maximum);
        return new($"*/{Format(interval)}", true);
    }

    public static CronFieldExpression Step(int start, int interval, int minimum, int maximum)
    {
        Validate(start, minimum, maximum, nameof(start));
        ValidateInterval(interval, maximum);
        return new($"{Format(start)}/{Format(interval)}", true);
    }

    public static CronFieldExpression Step(int start, int end, int interval, int minimum, int maximum)
    {
        Validate(start, minimum, maximum, nameof(start));
        Validate(end, minimum, maximum, nameof(end));
        ValidateInterval(interval, maximum);
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
