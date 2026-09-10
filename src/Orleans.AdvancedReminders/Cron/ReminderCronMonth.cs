#nullable enable
namespace Orleans.AdvancedReminders;

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
