#nullable enable
namespace Orleans.AdvancedReminders;

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
