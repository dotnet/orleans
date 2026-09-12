namespace Orleans.AdvancedReminders.Runtime;

/// <summary>
/// Action to apply when a reminder tick was missed.
/// </summary>
public enum MissedReminderAction : byte
{
    Skip = 0,
    FireImmediately = 1,
    Notify = 2,
}
