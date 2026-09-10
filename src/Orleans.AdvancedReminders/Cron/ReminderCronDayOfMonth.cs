#nullable enable
using System.Globalization;

namespace Orleans.AdvancedReminders;

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
