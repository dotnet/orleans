using System;

namespace Orleans.DurableReminders;

/// <summary>
/// Describes the schedule of a durable reminder.
/// </summary>
public sealed class ReminderSchedule
{
    private ReminderSchedule(
        Runtime.ReminderScheduleKind kind,
        TimeSpan? dueTime,
        DateTime? dueAtUtc,
        TimeSpan? period,
        string? cronExpression,
        string? cronTimeZoneId)
    {
        Kind = kind;
        DueTime = dueTime;
        DueAtUtc = dueAtUtc;
        Period = period;
        CronExpression = cronExpression;
        CronTimeZoneId = cronTimeZoneId;
    }

    public Runtime.ReminderScheduleKind Kind { get; }

    public TimeSpan? DueTime { get; }

    public DateTime? DueAtUtc { get; }

    public TimeSpan? Period { get; }

    public string? CronExpression { get; }

    public string? CronTimeZoneId { get; }

    public bool UsesAbsoluteDueTime => DueAtUtc.HasValue;

    public static ReminderSchedule Interval(TimeSpan dueTime, TimeSpan period)
        => new(Runtime.ReminderScheduleKind.Interval, dueTime, null, period, null, null);

    public static ReminderSchedule Interval(DateTime dueAtUtc, TimeSpan period)
        => new(Runtime.ReminderScheduleKind.Interval, null, dueAtUtc, period, null, null);

    public static ReminderSchedule Cron(string cronExpression, string? cronTimeZoneId = null)
        => new(Runtime.ReminderScheduleKind.Cron, null, null, null, cronExpression, cronTimeZoneId);
}
