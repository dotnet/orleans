using Orleans.AdvancedReminders.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>Convenience methods for registering one-shot reminders.</summary>
public static class GrainReminderOneShotExtensions
{
    /// <summary>Registers or updates a one-shot reminder to run after the specified delay.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this Grain grain, string reminderName, TimeSpan dueTime, MissedReminderAction action = MissedReminderAction.Skip)
        => grain.RegisterOrUpdateAdvancedReminder(reminderName, ReminderSchedule.OneShot(dueTime), action);

    /// <summary>Registers or updates a one-shot reminder to run at the specified UTC date and time.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this Grain grain, string reminderName, DateTime dueAtUtc, MissedReminderAction action = MissedReminderAction.Skip)
        => grain.RegisterOrUpdateAdvancedReminder(reminderName, ReminderSchedule.OneShot(dueAtUtc), action);

    /// <summary>Registers or updates a one-shot reminder to run at the instant represented by the specified offset-aware timestamp.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this Grain grain, string reminderName, DateTimeOffset dueAt, MissedReminderAction action = MissedReminderAction.Skip)
        => grain.RegisterOrUpdateAdvancedReminder(reminderName, ReminderSchedule.OneShot(dueAt), action);

    /// <summary>Registers or updates a one-shot reminder to run after the specified delay.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this IGrainBase grain, string reminderName, TimeSpan dueTime, MissedReminderAction action = MissedReminderAction.Skip)
        => grain.RegisterOrUpdateAdvancedReminder(reminderName, ReminderSchedule.OneShot(dueTime), action);

    /// <summary>Registers or updates a one-shot reminder to run at the specified UTC date and time.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this IGrainBase grain, string reminderName, DateTime dueAtUtc, MissedReminderAction action = MissedReminderAction.Skip)
        => grain.RegisterOrUpdateAdvancedReminder(reminderName, ReminderSchedule.OneShot(dueAtUtc), action);

    /// <summary>Registers or updates a one-shot reminder to run at the instant represented by the specified offset-aware timestamp.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateAdvancedReminder(
        this IGrainBase grain, string reminderName, DateTimeOffset dueAt, MissedReminderAction action = MissedReminderAction.Skip)
        => grain.RegisterOrUpdateAdvancedReminder(reminderName, ReminderSchedule.OneShot(dueAt), action);

}
