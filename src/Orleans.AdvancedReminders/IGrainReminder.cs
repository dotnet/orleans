namespace Orleans.AdvancedReminders;

/// <summary>
/// Handle for a persistent advanced reminder.
/// </summary>
public interface IGrainReminder
{
    string ReminderName { get; }

    /// <summary>
    /// Gets the cron expression, or <see langword="null"/> for an interval reminder.
    /// </summary>
    string? CronExpression { get; }

    /// <summary>
    /// Gets the cron time zone identifier, or <see langword="null"/> when the cron schedule uses UTC.
    /// </summary>
    string? CronTimeZone { get; }

    DurableJobPriority Priority { get; }

    Runtime.MissedReminderAction Action { get; }
}
