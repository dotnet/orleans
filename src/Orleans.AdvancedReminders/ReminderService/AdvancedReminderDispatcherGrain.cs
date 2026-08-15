using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableJobs;
using Orleans.Runtime;

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
        CancellationToken cancellationToken);

    Task EnsureScheduledAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken);
}

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
        CancellationToken cancellationToken)
    {
        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        await _reminderService.ProcessDueReminderCoreAsync(grainId, reminderName, expectedScheduleId, cancellationToken);
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

        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        await _reminderService.ProcessDueReminderCoreAsync(
            grainId,
            reminderName,
            scheduleId,
            cancellationToken,
            context.DequeueCount);
    }
}

internal sealed class ReminderDeliveryException(
    GrainId grainId,
    string reminderName,
    int durableJobDequeueCount,
    Exception innerException)
    : Exception(
        $"Reminder '{reminderName}' on grain '{grainId}' failed at Durable Jobs dequeue count {durableJobDequeueCount}.",
        innerException);
