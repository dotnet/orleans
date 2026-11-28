using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Functionality for managing advanced reminders.
/// </summary>
public interface IReminderService
{
    Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.MissedReminderAction action);

    Task UnregisterReminder(IGrainReminder reminder);

    Task<IGrainReminder?> GetReminder(GrainId grainId, string reminderName);

    Task<List<IGrainReminder>> GetReminders(GrainId grainId);
}
