using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Timers;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>Convenience methods for registering one-shot reminders through registries and services.</summary>
public static class ReminderOneShotRegistrationExtensions
{
    /// <summary>Registers or updates a one-shot reminder to run after the specified delay.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry, GrainId grainId, string reminderName, TimeSpan dueTime, MissedReminderAction action = MissedReminderAction.Skip)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.OneShot(dueTime), action);
    }

    /// <summary>Registers or updates a one-shot reminder to run at the specified UTC date and time.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry, GrainId grainId, string reminderName, DateTime dueAtUtc, MissedReminderAction action = MissedReminderAction.Skip)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.OneShot(dueAtUtc), action);
    }

    /// <summary>Registers or updates a one-shot reminder to run at the instant represented by the specified offset-aware timestamp.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderRegistry registry, GrainId grainId, string reminderName, DateTimeOffset dueAt, MissedReminderAction action = MissedReminderAction.Skip)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.OneShot(dueAt), action);
    }

    /// <summary>Registers or updates a one-shot reminder to run after the specified delay.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service, GrainId grainId, string reminderName, TimeSpan dueTime, MissedReminderAction action = MissedReminderAction.Skip)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.OneShot(dueTime), action);
    }

    /// <summary>Registers or updates a one-shot reminder to run at the specified UTC date and time.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service, GrainId grainId, string reminderName, DateTime dueAtUtc, MissedReminderAction action = MissedReminderAction.Skip)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.OneShot(dueAtUtc), action);
    }

    /// <summary>Registers or updates a one-shot reminder to run at the instant represented by the specified offset-aware timestamp.</summary>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(
        this IReminderService service, GrainId grainId, string reminderName, DateTimeOffset dueAt, MissedReminderAction action = MissedReminderAction.Skip)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.RegisterOrUpdateReminder(grainId, reminderName, ReminderSchedule.OneShot(dueAt), action);
    }

}
