#nullable enable
using System.Globalization;

namespace Orleans.AdvancedReminders;

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
        => new(CronFieldExpressionFormatter.Step(interval, 0, 7, sundayAlias: true));

    /// <summary>Matches every <paramref name="interval"/> calendar positions from <paramref name="start"/> through Sunday.</summary>
    public static ReminderCronDayOfWeek EveryFrom(DayOfWeek start, int interval)
        => new(CronFieldExpressionFormatter.Step(CronFieldExpressionFormatter.ToCronDay(start), interval, 0, 7, sundayAlias: true));

    /// <summary>Matches every <paramref name="interval"/> calendar positions within an inclusive range.</summary>
    public static ReminderCronDayOfWeek EveryBetween(DayOfWeek start, DayOfWeek end, int interval)
        => new(CronFieldExpressionFormatter.Step(
            CronFieldExpressionFormatter.ToCronDay(start),
            CronFieldExpressionFormatter.ToCronDay(end),
            interval,
            0,
            7,
            sundayAlias: true));

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
