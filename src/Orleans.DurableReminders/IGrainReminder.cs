namespace Orleans.DurableReminders;

/// <summary>
/// Handle for a persistent durable reminder.
/// </summary>
public interface IGrainReminder
{
    string ReminderName { get; }

    string CronExpression { get; }

    string CronTimeZone { get; }

    Runtime.ReminderPriority Priority { get; }

    Runtime.MissedReminderAction Action { get; }
}
