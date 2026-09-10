using System;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Describes the schedule of an advanced reminder.
/// </summary>
public sealed class ReminderSchedule
{
    private ReminderSchedule(
        Runtime.ReminderScheduleKind kind,
        TimeSpan? dueTime,
        DateTime? dueAtUtc,
        TimeSpan? period,
        string? cronExpression,
        string? cronTimeZoneId,
        bool isOneShot)
    {
        Kind = kind;
        DueTime = dueTime;
        DueAtUtc = dueAtUtc;
        Period = period;
        CronExpression = cronExpression;
        CronTimeZoneId = cronTimeZoneId;
        IsOneShot = isOneShot;
    }

    public Runtime.ReminderScheduleKind Kind { get; }

    public TimeSpan? DueTime { get; }

    public DateTime? DueAtUtc { get; }

    public TimeSpan? Period { get; }

    public string? CronExpression { get; }

    public string? CronTimeZoneId { get; }

    public bool UsesAbsoluteDueTime => DueAtUtc.HasValue;

    internal bool IsOneShot { get; }

    public static ReminderSchedule OneShot(TimeSpan dueTime)
        => new(Runtime.ReminderScheduleKind.Interval, dueTime, null, TimeSpan.Zero, null, null, isOneShot: true);

    public static ReminderSchedule OneShot(DateTime dueAtUtc)
    {
        EnsureUtc(dueAtUtc, nameof(dueAtUtc));
        return new(Runtime.ReminderScheduleKind.Interval, null, dueAtUtc, TimeSpan.Zero, null, null, isOneShot: true);
    }

    public static ReminderSchedule OneShot(DateTimeOffset dueAt)
        => OneShot(dueAt.UtcDateTime);

    public static ReminderSchedule Interval(TimeSpan dueTime, TimeSpan period)
        => new(Runtime.ReminderScheduleKind.Interval, dueTime, null, period, null, null, isOneShot: false);

    public static ReminderSchedule Interval(DateTime dueAtUtc, TimeSpan period)
        => new(Runtime.ReminderScheduleKind.Interval, null, dueAtUtc, period, null, null, isOneShot: false);

    public static ReminderSchedule Cron(string cronExpression, string? cronTimeZoneId = null)
        => new(Runtime.ReminderScheduleKind.Cron, null, null, null, cronExpression, cronTimeZoneId, isOneShot: false);

    private static void EnsureUtc(DateTime value, string argumentName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("DateTime must use DateTimeKind.Utc.", argumentName);
        }
    }
}
