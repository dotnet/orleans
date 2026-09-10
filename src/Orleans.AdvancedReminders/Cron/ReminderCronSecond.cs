#nullable enable
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
