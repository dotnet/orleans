#nullable enable
using System;
using System.Collections.Generic;

namespace Orleans;

/// <summary>
/// Provides typed helpers for building reminder cron expressions.
/// </summary>
public sealed class ReminderCronBuilder
{
    private readonly string _expression;
    private readonly TimeZoneInfo _timeZone;

    private ReminderCronBuilder(string expression, TimeZoneInfo timeZone)
    {
        _expression = expression;
        _timeZone = timeZone;
    }

    /// <summary>
    /// Uses a raw cron expression string.
    /// </summary>
    public static ReminderCronBuilder FromExpression(string expression)
        => FromExpression(expression, TimeZoneInfo.Utc);

    /// <summary>
    /// Uses a raw cron expression string and scheduling time zone.
    /// </summary>
    public static ReminderCronBuilder FromExpression(string expression, TimeZoneInfo? timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new ReminderCronBuilder(expression.Trim(), timeZone ?? TimeZoneInfo.Utc);
    }

    /// <summary>
    /// Every minute.
    /// </summary>
    public static ReminderCronBuilder EveryMinute() => new("* * * * *", TimeZoneInfo.Utc);

    /// <summary>
    /// At the specified minute of every hour.
    /// </summary>
    public static ReminderCronBuilder HourlyAt(int minute)
    {
        ValidateMinute(minute);
        return new($"{minute} * * * *", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// At the specified time every day.
    /// </summary>
    public static ReminderCronBuilder DailyAt(int hour, int minute)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} * * *", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// At the specified time Monday through Friday.
    /// </summary>
    public static ReminderCronBuilder WeekdaysAt(int hour, int minute)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} * * MON-FRI", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// At the specified time on the given day of week.
    /// </summary>
    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, int hour, int minute)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} * * {ToCronDay(dayOfWeek)}", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// At the specified time on the given day of month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, int hour, int minute)
    {
        ValidateDayOfMonth(dayOfMonth);
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} {dayOfMonth} * *", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// At the specified time on the last day of each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnLastDay(int hour, int minute)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        return new($"{minute} {hour} L * *", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// Returns a copy of this builder configured to evaluate occurrences in the provided time zone.
    /// </summary>
    public ReminderCronBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return new ReminderCronBuilder(_expression, timeZone);
    }

    /// <summary>
    /// Returns a copy of this builder configured to evaluate occurrences in the provided time zone id.
    /// </summary>
    public ReminderCronBuilder InTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        var zone = ResolveTimeZone(timeZoneId);
        return InTimeZone(zone);
    }

    /// <summary>
    /// Gets the time zone used by this builder for occurrence calculations.
    /// </summary>
    public TimeZoneInfo TimeZone => _timeZone;

    /// <summary>
    /// Gets the next occurrence in UTC, evaluated using this builder's time zone.
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime fromUtc, bool inclusive = false)
    {
        var expression = ReminderCronExpression.Parse(_expression);
        return IsUtcTimeZone(_timeZone)
            ? expression.GetNextOccurrence(fromUtc, inclusive)
            : expression.GetNextOccurrence(fromUtc, _timeZone, inclusive);
    }

    /// <summary>
    /// Gets all occurrences in UTC for the provided range, evaluated using this builder's time zone.
    /// </summary>
    public IEnumerable<DateTime> GetOccurrences(
        DateTime fromUtc,
        DateTime toUtc,
        bool fromInclusive = true,
        bool toInclusive = false)
    {
        var expression = ReminderCronExpression.Parse(_expression);
        return IsUtcTimeZone(_timeZone)
            ? expression.GetOccurrences(fromUtc, toUtc, fromInclusive, toInclusive)
            : expression.GetOccurrences(fromUtc, toUtc, _timeZone, fromInclusive, toInclusive);
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

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            }

            throw;
        }
    }

    private static bool IsUtcTimeZone(TimeZoneInfo zone)
        => string.Equals(zone.Id, TimeZoneInfo.Utc.Id, StringComparison.Ordinal);

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
