#nullable enable
using System.Globalization;

namespace Orleans.AdvancedReminders;

public sealed partial class ReminderCronBuilder
{
    /// <summary>
    /// Every second.
    /// </summary>
    public static ReminderCronBuilder EverySecond() => new("* * * * * *", TimeZoneInfo.Utc);

    /// <summary>
    /// Every specified second position within each minute.
    /// </summary>
    public static ReminderCronBuilder EverySeconds(int interval)
    {
        if (interval is < 1 or > 59)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Second interval must be in [1, 59].");
        }

        return new ReminderCronBuilder($"*/{interval.ToString(CultureInfo.InvariantCulture)} * * * * *", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// Every minute.
    /// </summary>
    public static ReminderCronBuilder EveryMinute() => new("* * * * *", TimeZoneInfo.Utc);

    /// <summary>
    /// Every specified minute position within each hour.
    /// </summary>
    public static ReminderCronBuilder EveryMinutes(int interval)
    {
        if (interval is < 1 or > 59)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Minute interval must be in [1, 59].");
        }

        return new ReminderCronBuilder($"*/{interval.ToString(CultureInfo.InvariantCulture)} * * * *", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// At the specified second of every minute.
    /// </summary>
    public static ReminderCronBuilder EveryMinuteAtSecond(int second)
    {
        ValidateSecond(second);
        return new ReminderCronBuilder($"{second.ToString(CultureInfo.InvariantCulture)} * * * * *", TimeZoneInfo.Utc);
    }

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
    /// Convenience overloads that apply a strongly typed scheduling time zone at creation time.
    /// </summary>
    public static ReminderCronBuilder EveryMinute(TimeZoneInfo timeZone) => EveryMinute().InTimeZone(timeZone);

    public static ReminderCronBuilder HourlyAt(int minute, TimeZoneInfo timeZone) => HourlyAt(minute).InTimeZone(timeZone);

    public static ReminderCronBuilder HourlyAt(int minute, int second, TimeZoneInfo timeZone) => HourlyAt(minute, second).InTimeZone(timeZone);

    public static ReminderCronBuilder HourlyAt(TimeSpan offset, TimeZoneInfo timeZone) => HourlyAt(offset).InTimeZone(timeZone);
}
