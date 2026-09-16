#nullable enable
namespace Orleans.AdvancedReminders;

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
