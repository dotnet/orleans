#nullable enable
using System.Globalization;

namespace Orleans.AdvancedReminders;

public sealed partial class ReminderCronBuilder
{
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

    private static string FormatDaysOfWeek(IEnumerable<DayOfWeek> daysOfWeek)
    {
        ArgumentNullException.ThrowIfNull(daysOfWeek);
        var values = new SortedSet<int>();
        foreach (var dayOfWeek in daysOfWeek)
        {
            values.Add(ToCronDay(dayOfWeek));
        }

        if (values.Count == 0)
        {
            throw new ArgumentException("At least one day of week is required.", nameof(daysOfWeek));
        }

        return FormatValues(values);
    }

    private static string FormatMonths(IEnumerable<int> months)
    {
        ArgumentNullException.ThrowIfNull(months);
        var values = new SortedSet<int>();
        foreach (var month in months)
        {
            ValidateMonth(month);
            values.Add(month);
        }

        if (values.Count == 0)
        {
            throw new ArgumentException("At least one month is required.", nameof(months));
        }

        return FormatValues(values);
    }

    private static string FormatValues(IEnumerable<int> values)
    {
        var formatted = new List<string>();
        foreach (var value in values)
        {
            formatted.Add(value.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(",", formatted);
    }




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
