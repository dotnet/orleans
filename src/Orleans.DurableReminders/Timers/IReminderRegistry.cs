using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.DurableReminders.Timers;

/// <summary>
/// Functionality for managing durable reminders from grain code.
/// </summary>
public interface IReminderRegistry
{
    Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId callingGrainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action);

    Task UnregisterReminder(GrainId callingGrainId, IGrainReminder reminder);

    Task<IGrainReminder?> GetReminder(GrainId callingGrainId, string reminderName);

    Task<List<IGrainReminder>> GetReminders(GrainId callingGrainId);
}
