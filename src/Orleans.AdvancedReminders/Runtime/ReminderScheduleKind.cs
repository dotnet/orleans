namespace Orleans.AdvancedReminders.Runtime;

/// <summary>
/// Represents the schedule type of an advanced reminder.
/// </summary>
public enum ReminderScheduleKind : byte
{
    Interval = 0,
    Cron = 1,
}
