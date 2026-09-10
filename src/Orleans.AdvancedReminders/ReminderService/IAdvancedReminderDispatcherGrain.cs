namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAdvancedReminderDispatcherGrain : IGrainWithStringKey, IDurableJobHandler
{
    Task<IGrainReminder> RegisterOrUpdateAsync(ReminderEntry entry);

    Task<IGrainReminder> ReconcileAttributeAsync(ReminderEntry entry, string declarationId);

    Task<string> UpsertAndScheduleAsync(ReminderEntry entry, CancellationToken cancellationToken);

    Task UnregisterAsync(ReminderData reminder);

    Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        int durableJobDequeueCount,
        CancellationToken cancellationToken);

    Task EnsureScheduledAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken);
}
