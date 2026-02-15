#nullable enable
using System;

namespace Orleans;

/// <summary>
/// Provides typed helpers for building reminder cron expressions.
/// </summary>
public sealed class ReminderCronBuilder
{
    private readonly string _expression;

    private ReminderCronBuilder(string expression)
    {
        _expression = expression;
    }

    /// <summary>
    /// Uses a raw cron expression string.
    /// </summary>
    public static ReminderCronBuilder FromExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new ReminderCronBuilder(expression.Trim());
    }

    /// <summary>
    /// Every minute.
    /// </summary>
    public static ReminderCronBuilder EveryMinute() => new("* * * * *");

    /// <summary>
    /// At the specified minute of every hour.
    /// </summary>
    public static ReminderCronBuilder HourlyAt(int minute)
    {
        ValidateMinute(minute);
        return new($"{minute} * * * *");
    }

    /// <summary>
    /// At the specified time every day.
    /// </summary>
    public static ReminderCronBuilder DailyAt(int hour, int minute)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} * * *");
    }

    /// <summary>
    /// At the specified time Monday through Friday.
    /// </summary>
    public static ReminderCronBuilder WeekdaysAt(int hour, int minute)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} * * MON-FRI");
    }

    /// <summary>
    /// At the specified time on the given day of week.
    /// </summary>
    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, int hour, int minute)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} * * {ToCronDay(dayOfWeek)}");
    }

    /// <summary>
    /// At the specified time on the given day of month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, int hour, int minute)
    {
        ValidateDayOfMonth(dayOfMonth);
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} {dayOfMonth} * *");
    }

    /// <summary>
    /// At the specified time on the last day of each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnLastDay(int hour, int minute)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} L * *");
    }

    /// <summary>
    /// Returns the resulting cron expression string.
    /// </summary>
    public string ToExpressionString() => _expression;

    /// <summary>
    /// Parses and validates the builder output as a typed cron expression.
    /// </summary>
    public ReminderCronExpression ToCronExpression() => ReminderCronExpression.Parse(_expression);

    /// <summary>
    /// Alias for <see cref="ToCronExpression"/>.
    /// </summary>
    public ReminderCronExpression Build() => ToCronExpression();

    private static int ToCronDay(DayOfWeek dayOfWeek)
    {
        // Unix cron mapping: 0 or 7 = Sunday, 1 = Monday, ..., 6 = Saturday
        return dayOfWeek switch
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
    }

    private static void ValidateMinute(int minute)
    {
        if (minute is < 0 or > 59)
        {
            throw new ArgumentOutOfRangeException(nameof(minute), minute, "Minute must be in [0, 59].");
        }
    }

    private static void ValidateHour(int hour)
    {
        if (hour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(hour), hour, "Hour must be in [0, 23].");
        }
    }

    private static void ValidateDayOfMonth(int dayOfMonth)
    {
        if (dayOfMonth is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(dayOfMonth), dayOfMonth, "Day of month must be in [1, 31].");
        }
    }
}
