#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orleans.AdvancedReminders;

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
    public static ReminderCronBuilder HourlyAt(int minute) => CreateHourly(minute, second: 0);

    /// <summary>
    /// At the specified minute and second of every hour.
    /// </summary>
    public static ReminderCronBuilder HourlyAt(int minute, int second) => CreateHourly(minute, second);

    /// <summary>
    /// At the specified offset within every hour.
    /// </summary>
    public static ReminderCronBuilder HourlyAt(TimeSpan offset)
    {
        var (minute, second) = GetHourlyOffsetParts(offset, nameof(offset));
        return CreateHourly(minute, second);
    }

    /// <summary>
    /// At the specified time every day.
    /// </summary>
    public static ReminderCronBuilder DailyAt(int hour, int minute) => CreateSchedule(hour, minute, "*", "*", "*");

    /// <summary>
    /// At the specified time every day.
    /// </summary>
    public static ReminderCronBuilder DailyAt(int hour, int minute, int second) => CreateSchedule(hour, minute, "*", "*", "*", second);

    /// <summary>
    /// At the specified time every day.
    /// </summary>
    public static ReminderCronBuilder DailyAt(TimeOnly time)
        => CreateSchedule(time, "*", "*", "*", nameof(time));

    /// <summary>
    /// At the specified time every day.
    /// </summary>
    public static ReminderCronBuilder DailyAt(TimeSpan timeOfDay)
        => CreateSchedule(timeOfDay, "*", "*", "*", nameof(timeOfDay));

    /// <summary>
    /// At the specified time Monday through Friday.
    /// </summary>
    public static ReminderCronBuilder WeekdaysAt(int hour, int minute) => CreateSchedule(hour, minute, "*", "*", "MON-FRI");

    /// <summary>
    /// At the specified time Monday through Friday.
    /// </summary>
    public static ReminderCronBuilder WeekdaysAt(int hour, int minute, int second) => CreateSchedule(hour, minute, "*", "*", "MON-FRI", second);

    /// <summary>
    /// At the specified time Monday through Friday.
    /// </summary>
    public static ReminderCronBuilder WeekdaysAt(TimeOnly time)
        => CreateSchedule(time, "*", "*", "MON-FRI", nameof(time));

    /// <summary>
    /// At the specified time Monday through Friday.
    /// </summary>
    public static ReminderCronBuilder WeekdaysAt(TimeSpan timeOfDay)
        => CreateSchedule(timeOfDay, "*", "*", "MON-FRI", nameof(timeOfDay));

    /// <summary>
    /// At the specified time on Saturday and Sunday.
    /// </summary>
    public static ReminderCronBuilder WeekendsAt(int hour, int minute) => CreateSchedule(hour, minute, "*", "*", "SAT,SUN");

    /// <summary>
    /// At the specified time on Saturday and Sunday.
    /// </summary>
    public static ReminderCronBuilder WeekendsAt(int hour, int minute, int second) => CreateSchedule(hour, minute, "*", "*", "SAT,SUN", second);

    /// <summary>
    /// At the specified time on Saturday and Sunday.
    /// </summary>
    public static ReminderCronBuilder WeekendsAt(TimeOnly time)
        => CreateSchedule(time, "*", "*", "SAT,SUN", nameof(time));

    /// <summary>
    /// At the specified time on Saturday and Sunday.
    /// </summary>
    public static ReminderCronBuilder WeekendsAt(TimeSpan timeOfDay)
        => CreateSchedule(timeOfDay, "*", "*", "SAT,SUN", nameof(timeOfDay));

    /// <summary>
    /// At the specified time on the given day of week.
    /// </summary>
    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, int hour, int minute)
        => CreateSchedule(hour, minute, "*", "*", ToCronDay(dayOfWeek).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// At the specified time on the given day of week.
    /// </summary>
    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, int hour, int minute, int second)
        => CreateSchedule(hour, minute, "*", "*", ToCronDay(dayOfWeek).ToString(CultureInfo.InvariantCulture), second);

    /// <summary>
    /// At the specified time on the given day of week.
    /// </summary>
    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, TimeOnly time)
        => CreateSchedule(time, "*", "*", ToCronDay(dayOfWeek).ToString(CultureInfo.InvariantCulture), nameof(time));

    /// <summary>
    /// At the specified time on the given day of week.
    /// </summary>
    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, TimeSpan timeOfDay)
        => CreateSchedule(timeOfDay, "*", "*", ToCronDay(dayOfWeek).ToString(CultureInfo.InvariantCulture), nameof(timeOfDay));

    /// <summary>
    /// At the specified time on the given day of month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, int hour, int minute)
    {
        ValidateDayOfMonth(dayOfMonth);
        return CreateSchedule(hour, minute, dayOfMonth.ToString(CultureInfo.InvariantCulture), "*", "*");
    }

    /// <summary>
    /// At the specified time on the given day of month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, int hour, int minute, int second)
    {
        ValidateDayOfMonth(dayOfMonth);
        return CreateSchedule(hour, minute, dayOfMonth.ToString(CultureInfo.InvariantCulture), "*", "*", second);
    }

    /// <summary>
    /// At the specified time on the given day of month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, TimeOnly time)
    {
        ValidateDayOfMonth(dayOfMonth);
        return CreateSchedule(time, dayOfMonth.ToString(CultureInfo.InvariantCulture), "*", "*", nameof(time));
    }

    /// <summary>
    /// At the specified time on the given day of month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, TimeSpan timeOfDay)
    {
        ValidateDayOfMonth(dayOfMonth);
        return CreateSchedule(timeOfDay, dayOfMonth.ToString(CultureInfo.InvariantCulture), "*", "*", nameof(timeOfDay));
    }

    /// <summary>
    /// At the specified time on the last day of each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnLastDay(int hour, int minute) => CreateSchedule(hour, minute, "L", "*", "*");

    /// <summary>
    /// At the specified time on the last day of each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnLastDay(int hour, int minute, int second) => CreateSchedule(hour, minute, "L", "*", "*", second);

    /// <summary>
    /// At the specified time on the last day of each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnLastDay(TimeOnly time)
        => CreateSchedule(time, "L", "*", "*", nameof(time));

    /// <summary>
    /// At the specified time on the last day of each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnLastDay(TimeSpan timeOfDay)
        => CreateSchedule(timeOfDay, "L", "*", "*", nameof(timeOfDay));

    /// <summary>
    /// At the specified time on the given month/day every year.
    /// </summary>
    public static ReminderCronBuilder YearlyOn(int month, int dayOfMonth, int hour, int minute)
    {
        ValidateMonth(month);
        ValidateDayOfMonth(dayOfMonth, month);
        return CreateSchedule(hour, minute, dayOfMonth.ToString(CultureInfo.InvariantCulture), month.ToString(CultureInfo.InvariantCulture), "*");
    }

    /// <summary>
    /// At the specified time on the given month/day every year.
    /// </summary>
    public static ReminderCronBuilder YearlyOn(int month, int dayOfMonth, int hour, int minute, int second)
    {
        ValidateMonth(month);
        ValidateDayOfMonth(dayOfMonth, month);
        return CreateSchedule(hour, minute, dayOfMonth.ToString(CultureInfo.InvariantCulture), month.ToString(CultureInfo.InvariantCulture), "*", second);
    }

    /// <summary>
    /// At the specified time on the given month/day every year.
    /// </summary>
    public static ReminderCronBuilder YearlyOn(int month, int dayOfMonth, TimeOnly time)
    {
        ValidateMonth(month);
        ValidateDayOfMonth(dayOfMonth, month);
        return CreateSchedule(
            time,
            dayOfMonth.ToString(CultureInfo.InvariantCulture),
            month.ToString(CultureInfo.InvariantCulture),
            "*",
            nameof(time));
    }

    /// <summary>
    /// At the specified time on the given month/day every year.
    /// </summary>
    public static ReminderCronBuilder YearlyOn(int month, int dayOfMonth, TimeSpan timeOfDay)
    {
        ValidateMonth(month);
        ValidateDayOfMonth(dayOfMonth, month);
        return CreateSchedule(
            timeOfDay,
            dayOfMonth.ToString(CultureInfo.InvariantCulture),
            month.ToString(CultureInfo.InvariantCulture),
            "*",
            nameof(timeOfDay));
    }

    /// <summary>
    /// At the specified time on the given date's month/day every year. The year component is ignored.
    /// </summary>
    public static ReminderCronBuilder YearlyOn(DateOnly date, int hour, int minute) => YearlyOn(date.Month, date.Day, hour, minute);

    /// <summary>
    /// At the specified time on the given date's month/day every year. The year component is ignored.
    /// </summary>
    public static ReminderCronBuilder YearlyOn(DateOnly date, int hour, int minute, int second) => YearlyOn(date.Month, date.Day, hour, minute, second);

    /// <summary>
    /// At the specified time on the given date's month/day every year. The year component is ignored.
    /// </summary>
    public static ReminderCronBuilder YearlyOn(DateOnly date, TimeOnly time) => YearlyOn(date.Month, date.Day, time);

    /// <summary>
    /// At the specified time on the given date's month/day every year. The year component is ignored.
    /// </summary>
    public static ReminderCronBuilder YearlyOn(DateOnly date, TimeSpan timeOfDay) => YearlyOn(date.Month, date.Day, timeOfDay);

    /// <summary>
    /// Convenience overloads that apply a strongly typed scheduling time zone at creation time.
    /// </summary>
    public static ReminderCronBuilder EveryMinute(TimeZoneInfo timeZone) => EveryMinute().InTimeZone(timeZone);

    public static ReminderCronBuilder HourlyAt(int minute, TimeZoneInfo timeZone) => HourlyAt(minute).InTimeZone(timeZone);

    public static ReminderCronBuilder HourlyAt(int minute, int second, TimeZoneInfo timeZone) => HourlyAt(minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder HourlyAt(TimeSpan offset, TimeZoneInfo timeZone) => HourlyAt(offset).InTimeZone(timeZone);

    public static ReminderCronBuilder DailyAt(int hour, int minute, TimeZoneInfo timeZone) => DailyAt(hour, minute).InTimeZone(timeZone);

    public static ReminderCronBuilder DailyAt(int hour, int minute, int second, TimeZoneInfo timeZone) => DailyAt(hour, minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder DailyAt(TimeOnly time, TimeZoneInfo timeZone) => DailyAt(time).InTimeZone(timeZone);

    public static ReminderCronBuilder DailyAt(TimeSpan timeOfDay, TimeZoneInfo timeZone) => DailyAt(timeOfDay).InTimeZone(timeZone);

    public static ReminderCronBuilder WeekdaysAt(int hour, int minute, TimeZoneInfo timeZone) => WeekdaysAt(hour, minute).InTimeZone(timeZone);

    public static ReminderCronBuilder WeekdaysAt(int hour, int minute, int second, TimeZoneInfo timeZone) => WeekdaysAt(hour, minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder WeekdaysAt(TimeOnly time, TimeZoneInfo timeZone) => WeekdaysAt(time).InTimeZone(timeZone);

    public static ReminderCronBuilder WeekdaysAt(TimeSpan timeOfDay, TimeZoneInfo timeZone) => WeekdaysAt(timeOfDay).InTimeZone(timeZone);

    public static ReminderCronBuilder WeekendsAt(int hour, int minute, TimeZoneInfo timeZone) => WeekendsAt(hour, minute).InTimeZone(timeZone);

    public static ReminderCronBuilder WeekendsAt(int hour, int minute, int second, TimeZoneInfo timeZone) => WeekendsAt(hour, minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder WeekendsAt(TimeOnly time, TimeZoneInfo timeZone) => WeekendsAt(time).InTimeZone(timeZone);

    public static ReminderCronBuilder WeekendsAt(TimeSpan timeOfDay, TimeZoneInfo timeZone) => WeekendsAt(timeOfDay).InTimeZone(timeZone);

    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, int hour, int minute, TimeZoneInfo timeZone) => WeeklyOn(dayOfWeek, hour, minute).InTimeZone(timeZone);

    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, int hour, int minute, int second, TimeZoneInfo timeZone) => WeeklyOn(dayOfWeek, hour, minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, TimeOnly time, TimeZoneInfo timeZone) => WeeklyOn(dayOfWeek, time).InTimeZone(timeZone);

    public static ReminderCronBuilder WeeklyOn(DayOfWeek dayOfWeek, TimeSpan timeOfDay, TimeZoneInfo timeZone) => WeeklyOn(dayOfWeek, timeOfDay).InTimeZone(timeZone);

    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, int hour, int minute, TimeZoneInfo timeZone) => MonthlyOn(dayOfMonth, hour, minute).InTimeZone(timeZone);

    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, int hour, int minute, int second, TimeZoneInfo timeZone) => MonthlyOn(dayOfMonth, hour, minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, TimeOnly time, TimeZoneInfo timeZone) => MonthlyOn(dayOfMonth, time).InTimeZone(timeZone);

    public static ReminderCronBuilder MonthlyOn(int dayOfMonth, TimeSpan timeOfDay, TimeZoneInfo timeZone) => MonthlyOn(dayOfMonth, timeOfDay).InTimeZone(timeZone);

    public static ReminderCronBuilder MonthlyOnLastDay(int hour, int minute, TimeZoneInfo timeZone) => MonthlyOnLastDay(hour, minute).InTimeZone(timeZone);

    public static ReminderCronBuilder MonthlyOnLastDay(int hour, int minute, int second, TimeZoneInfo timeZone) => MonthlyOnLastDay(hour, minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder MonthlyOnLastDay(TimeOnly time, TimeZoneInfo timeZone) => MonthlyOnLastDay(time).InTimeZone(timeZone);

    public static ReminderCronBuilder MonthlyOnLastDay(TimeSpan timeOfDay, TimeZoneInfo timeZone) => MonthlyOnLastDay(timeOfDay).InTimeZone(timeZone);

    public static ReminderCronBuilder YearlyOn(int month, int dayOfMonth, int hour, int minute, TimeZoneInfo timeZone) => YearlyOn(month, dayOfMonth, hour, minute).InTimeZone(timeZone);

    public static ReminderCronBuilder YearlyOn(int month, int dayOfMonth, int hour, int minute, int second, TimeZoneInfo timeZone) => YearlyOn(month, dayOfMonth, hour, minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder YearlyOn(int month, int dayOfMonth, TimeOnly time, TimeZoneInfo timeZone) => YearlyOn(month, dayOfMonth, time).InTimeZone(timeZone);

    public static ReminderCronBuilder YearlyOn(int month, int dayOfMonth, TimeSpan timeOfDay, TimeZoneInfo timeZone) => YearlyOn(month, dayOfMonth, timeOfDay).InTimeZone(timeZone);

    public static ReminderCronBuilder YearlyOn(DateOnly date, int hour, int minute, TimeZoneInfo timeZone) => YearlyOn(date, hour, minute).InTimeZone(timeZone);

    public static ReminderCronBuilder YearlyOn(DateOnly date, int hour, int minute, int second, TimeZoneInfo timeZone) => YearlyOn(date, hour, minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder YearlyOn(DateOnly date, TimeOnly time, TimeZoneInfo timeZone) => YearlyOn(date, time).InTimeZone(timeZone);

    public static ReminderCronBuilder YearlyOn(DateOnly date, TimeSpan timeOfDay, TimeZoneInfo timeZone) => YearlyOn(date, timeOfDay).InTimeZone(timeZone);

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

    private static ReminderCronBuilder CreateHourly(int minute, int second)
    {
        ValidateMinute(minute);
        ValidateSecond(second);
        return CreateSchedule(
            minute.ToString(CultureInfo.InvariantCulture),
            "*",
            "*",
            "*",
            "*",
            second);
    }

    private static ReminderCronBuilder CreateSchedule(int hour, int minute, string dayOfMonth, string month, string dayOfWeek, int second = 0)
    {
        ValidateHour(hour);
        ValidateMinute(minute);
        ValidateSecond(second);
        return CreateSchedule(
            minute.ToString(CultureInfo.InvariantCulture),
            hour.ToString(CultureInfo.InvariantCulture),
            dayOfMonth,
            month,
            dayOfWeek,
            second);
    }

    private static ReminderCronBuilder CreateSchedule(TimeOnly time, string dayOfMonth, string month, string dayOfWeek, string paramName)
    {
        ValidateWholeSeconds(time.Ticks, paramName);
        return CreateSchedule(time.Hour, time.Minute, dayOfMonth, month, dayOfWeek, time.Second);
    }

    private static ReminderCronBuilder CreateSchedule(TimeSpan timeOfDay, string dayOfMonth, string month, string dayOfWeek, string paramName)
    {
        var (hour, minute, second) = GetTimeOfDayParts(timeOfDay, paramName);
        return CreateSchedule(hour, minute, dayOfMonth, month, dayOfWeek, second);
    }

    private static ReminderCronBuilder CreateSchedule(string minute, string hour, string dayOfMonth, string month, string dayOfWeek, int second)
    {
        var expression = second == 0
            ? $"{minute} {hour} {dayOfMonth} {month} {dayOfWeek}"
            : $"{second.ToString(CultureInfo.InvariantCulture)} {minute} {hour} {dayOfMonth} {month} {dayOfWeek}";
        return new ReminderCronBuilder(expression, TimeZoneInfo.Utc);
    }

    private static (int Hour, int Minute, int Second) GetTimeOfDayParts(TimeSpan timeOfDay, string paramName)
    {
        if (timeOfDay < TimeSpan.Zero || timeOfDay >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(paramName, timeOfDay, "Time of day must be in [00:00:00, 24:00:00).");
        }

        ValidateWholeSeconds(timeOfDay.Ticks, paramName);
        return ((int)timeOfDay.TotalHours, timeOfDay.Minutes, timeOfDay.Seconds);
    }

    private static (int Minute, int Second) GetHourlyOffsetParts(TimeSpan offset, string paramName)
    {
        if (offset < TimeSpan.Zero || offset >= TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(paramName, offset, "Hourly offset must be in [00:00:00, 01:00:00).");
        }

        ValidateWholeSeconds(offset.Ticks, paramName);
        return (offset.Minutes, offset.Seconds);
    }

    private static void ValidateWholeSeconds(long ticks, string paramName)
    {
        if (ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "Sub-second precision is not supported.");
        }
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

    private static void ValidateDayOfMonth(int dayOfMonth, int month)
    {
        ValidateDayOfMonth(dayOfMonth);
        if (dayOfMonth > DateTime.DaysInMonth(2024, month))
        {
            throw new ArgumentOutOfRangeException(nameof(dayOfMonth), dayOfMonth, $"Day of month must be valid for month {month}.");
        }
    }

    private static void ValidateMonth(int month)
    {
        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be in [1, 12].");
        }
    }

    private static void ValidateSecond(int second)
    {
        if (second is < 0 or > 59)
        {
            throw new ArgumentOutOfRangeException(nameof(second), second, "Second must be in [0, 59].");
        }
    }
}
