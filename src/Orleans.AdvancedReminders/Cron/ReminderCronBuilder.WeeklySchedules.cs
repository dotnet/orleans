#nullable enable
using System.Globalization;

namespace Orleans.AdvancedReminders;

public sealed partial class ReminderCronBuilder
{
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
    /// At the specified time on each selected day of week.
    /// </summary>
    public static ReminderCronBuilder WeeklyOn(IEnumerable<DayOfWeek> daysOfWeek, TimeOnly time)
        => CreateSchedule(time, "*", "*", FormatDaysOfWeek(daysOfWeek), nameof(time));

    /// <summary>
    /// At the specified time on weekdays in each selected month.
    /// </summary>
    public static ReminderCronBuilder WeekdaysInMonthsAt(IEnumerable<int> months, TimeOnly time)
        => CreateSchedule(time, "?", FormatMonths(months), "MON-FRI", nameof(time));

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
}
