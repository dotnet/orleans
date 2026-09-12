#nullable enable
namespace Orleans.AdvancedReminders;

/// <summary>
/// Status categories used by <see cref="ReminderQueryFilter"/> for due-state filtering.
/// </summary>
[Flags]
public enum ReminderQueryStatus : byte
{
    /// <summary>
    /// No status filtering.
    /// </summary>
    Any = 0,

    /// <summary>
    /// Matches reminders whose due time is less than or equal to now.
    /// </summary>
    Due = 1 << 0,

    /// <summary>
    /// Matches reminders overdue by at least <see cref="ReminderQueryFilter.OverdueBy"/>.
    /// </summary>
    Overdue = 1 << 1,

    /// <summary>
    /// Matches reminders considered missed: due time older than <see cref="ReminderQueryFilter.MissedBy"/>
    /// and a last-fire timestamp earlier than that due time (or missing).
    /// </summary>
    Missed = 1 << 2,

    /// <summary>
    /// Matches reminders due strictly after now.
    /// </summary>
    Upcoming = 1 << 3,
}
