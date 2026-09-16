namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class AdvancedReminderDispatcherGrain(
    AdvancedReminderService reminderService) : Grain, IAdvancedReminderDispatcherGrain
{
    private readonly AdvancedReminderService _reminderService = reminderService;

    public Task<IGrainReminder> RegisterOrUpdateAsync(ReminderEntry entry)
        => _reminderService.RegisterOrUpdateCoreAsync(entry, CancellationToken.None);

    public Task<IGrainReminder> ReconcileAttributeAsync(ReminderEntry entry, string declarationId)
        => _reminderService.ReconcileAttributeCoreAsync(entry, declarationId, CancellationToken.None);

    public Task<string> UpsertAndScheduleAsync(ReminderEntry entry, CancellationToken cancellationToken)
        => _reminderService.UpsertAndScheduleCoreAsync(entry, cancellationToken);

    public Task UnregisterAsync(ReminderData reminder)
        => _reminderService.UnregisterCoreAsync(reminder, CancellationToken.None);

    public async Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        int durableJobDequeueCount,
        CancellationToken cancellationToken)
    {
        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        await _reminderService.ProcessDueReminderCoreAsync(grainId, reminderName, expectedScheduleId, cancellationToken, durableJobDequeueCount);
    }

    public Task EnsureScheduledAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken)
        => _reminderService.EnsureScheduledCoreAsync(grainId, reminderName, expectedScheduleId, force, cancellationToken);

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (!AdvancedReminderService.TryGetReminderMetadata(context.Job.Metadata, out var grainId, out var reminderName, out var scheduleId))
        {
            return;
        }

        // Durable Jobs invokes handlers through an always-interleaving grain extension.
        // Queue a normal dispatcher request so delivery waits for registration and handle
        // persistence, including when the job becomes due before ScheduleJobAsync returns.
        using var reentrancy = RequestContext.SuppressCallChainReentrancy();
        await _reminderService.ProcessDueReminderAsync(
            grainId,
            reminderName,
            scheduleId,
            cancellationToken,
            context.DequeueCount);
    }
}
