using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Functionality for managing durable reminders.
/// </summary>
public interface IReminderService
{
    Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action);

    Task UnregisterReminder(IGrainReminder reminder);

    Task<IGrainReminder?> GetReminder(GrainId grainId, string reminderName);

    Task<List<IGrainReminder>> GetReminders(GrainId grainId);

    Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedETag,
        CancellationToken cancellationToken);
}
