using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAdvancedReminderDispatcherGrain : IGrainWithStringKey, IDurableJobHandler
{
    Task<IGrainReminder> RegisterOrUpdateAsync(ReminderEntry entry);

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

[KeepAlive]
internal sealed class AdvancedReminderDispatcherGrain(
    AdvancedReminderService reminderService,
    ILogger<AdvancedReminderDispatcherGrain> logger) : Grain, IAdvancedReminderDispatcherGrain
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private readonly AdvancedReminderService _reminderService = reminderService;
    private readonly ILogger<AdvancedReminderDispatcherGrain> _logger = logger;
    private readonly Dictionary<string, IGrainTimer> _retryTimers = new(StringComparer.Ordinal);

    public async Task<IGrainReminder> RegisterOrUpdateAsync(ReminderEntry entry)
    {
        try
        {
            var result = await _reminderService.RegisterOrUpdateCoreAsync(entry, CancellationToken.None);
            ClearRetry(entry.ReminderName);
            return result;
        }
        catch
        {
            ScheduleRetry(entry.GrainId, entry.ReminderName, entry.ScheduleId);
            throw;
        }
    }

    public async Task<string> UpsertAndScheduleAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reminderService.UpsertAndScheduleCoreAsync(entry, cancellationToken);
            ClearRetry(entry.ReminderName);
            return result;
        }
        catch
        {
            ScheduleRetry(entry.GrainId, entry.ReminderName, entry.ScheduleId);
            throw;
        }
    }

    public async Task UnregisterAsync(ReminderData reminder)
    {
        await _reminderService.UnregisterCoreAsync(reminder, CancellationToken.None);
        ClearRetry(reminder.ReminderName);
    }

    public async Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _reminderService.ProcessDueReminderCoreAsync(grainId, reminderName, expectedScheduleId, cancellationToken);
            ClearRetry(reminderName);
        }
        catch
        {
            ScheduleRetry(grainId, reminderName, expectedScheduleId);
            throw;
        }
    }

    public async Task EnsureScheduledAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken)
    {
        try
        {
            await _reminderService.EnsureScheduledCoreAsync(grainId, reminderName, expectedScheduleId, force, cancellationToken);
            ClearRetry(reminderName);
        }
        catch
        {
            ScheduleRetry(grainId, reminderName, expectedScheduleId);
            throw;
        }
    }

    public Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (!AdvancedReminderService.TryGetReminderMetadata(context.Job.Metadata, out var grainId, out var reminderName, out var scheduleId))
        {
            return Task.CompletedTask;
        }

        return ProcessDueReminderAsync(grainId, reminderName, scheduleId, cancellationToken);
    }

    private void ScheduleRetry(GrainId grainId, string reminderName, string? scheduleId)
    {
        ClearRetry(reminderName);
        var state = new RetryState(grainId, reminderName, scheduleId);
        _retryTimers[reminderName] = this.RegisterGrainTimer(
            RetryAsync,
            state,
            new GrainTimerCreationOptions
            {
                DueTime = RetryDelay,
                Period = Timeout.InfiniteTimeSpan,
                KeepAlive = true,
            });
    }

    private async Task RetryAsync(RetryState state, CancellationToken cancellationToken)
    {
        ClearRetry(state.ReminderName);
        try
        {
            await _reminderService.EnsureScheduledCoreAsync(
                state.GrainId,
                state.ReminderName,
                state.ScheduleId,
                force: false,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Retrying durable job creation for reminder {ReminderName} on grain {GrainId}.",
                state.ReminderName,
                state.GrainId);
            ScheduleRetry(state.GrainId, state.ReminderName, state.ScheduleId);
        }
    }

    private void ClearRetry(string reminderName)
    {
        if (_retryTimers.Remove(reminderName, out var timer))
        {
            timer.Dispose();
        }
    }

    private sealed record RetryState(GrainId GrainId, string ReminderName, string? ScheduleId);
}
