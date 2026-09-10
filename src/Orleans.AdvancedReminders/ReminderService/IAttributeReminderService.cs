namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAttributeReminderService
{
    Task<IGrainReminder> ReconcileReminder(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        MissedReminderAction action,
        string declarationId);
}
