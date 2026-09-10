#nullable enable
using System.Globalization;

namespace Orleans.AdvancedReminders;

public sealed partial class ReminderCronBuilder
{
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
    /// At the specified time on the weekday nearest to the selected day of month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnNearestWeekday(int dayOfMonth, TimeOnly time)
    {
        ValidateDayOfMonth(dayOfMonth);
        return CreateSchedule(
            time,
            $"{dayOfMonth.ToString(CultureInfo.InvariantCulture)}W",
            "*",
            "*",
            nameof(time));
    }

    /// <summary>
    /// At the specified time a fixed number of days before the last day of each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyBeforeLastDay(int daysBeforeLastDay, TimeOnly time)
    {
        if (daysBeforeLastDay is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(daysBeforeLastDay),
                daysBeforeLastDay,
                "Days before the last day must be in [1, 30].");
        }

        return CreateSchedule(
            time,
            $"L-{daysBeforeLastDay.ToString(CultureInfo.InvariantCulture)}",
            "*",
            "*",
            nameof(time));
    }

    /// <summary>
    /// At the specified time on the last occurrence of a selected weekday in each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnLast(DayOfWeek dayOfWeek, TimeOnly time)
        => CreateSchedule(
            time,
            "?",
            "*",
            $"{ToCronDay(dayOfWeek).ToString(CultureInfo.InvariantCulture)}L",
            nameof(time));

    /// <summary>
    /// At the specified time on the selected occurrence of a weekday in each month.
    /// </summary>
    public static ReminderCronBuilder MonthlyOnNth(DayOfWeek dayOfWeek, int occurrence, TimeOnly time)
    {
        if (occurrence is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence), occurrence, "Occurrence must be in [1, 5].");
        }

        return CreateSchedule(
            time,
            "?",
            "*",
            $"{ToCronDay(dayOfWeek).ToString(CultureInfo.InvariantCulture)}#{occurrence.ToString(CultureInfo.InvariantCulture)}",
            nameof(time));
    }

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
}
