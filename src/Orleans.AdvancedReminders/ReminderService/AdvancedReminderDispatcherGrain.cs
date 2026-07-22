using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

internal sealed class AdvancedReminderDispatcherGrain(
    AdvancedReminderService reminderService,
    ILogger<AdvancedReminderDispatcherGrain> logger,
    IOptions<ReminderOptions>? options = null) : Grain, IAdvancedReminderDispatcherGrain
{
    private readonly AdvancedReminderService _reminderService = reminderService;
    private readonly ILogger<AdvancedReminderDispatcherGrain> _logger = logger;
    private readonly ReminderOptions _options = options?.Value ?? new ReminderOptions();
    private readonly Dictionary<string, RetryRegistration> _retryTimers = new(StringComparer.Ordinal);

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
            ScheduleRetry(entry.GrainId, entry.ReminderName, entry.ScheduleId, force: false);
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
            ScheduleRetry(entry.GrainId, entry.ReminderName, entry.ScheduleId, force: false);
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
            // Processing a due reminder can persist the next occurrence before durable-job
            // scheduling fails. Reconcile the current row rather than pinning the retry to
            // the schedule id of the occurrence which just ran.
            ScheduleRetry(grainId, reminderName, scheduleId: null, force: true);
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
            ScheduleRetry(grainId, reminderName, expectedScheduleId, force);
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

    private void ScheduleRetry(
        GrainId grainId,
        string reminderName,
        string? scheduleId,
        bool force,
        int minimumAttempt = 0)
    {
        var attempt = minimumAttempt;
        if (_retryTimers.Remove(reminderName, out var existing))
        {
            attempt = Math.Max(attempt, existing.State.Attempt + 1);
            existing.Timer.Dispose();
        }

        var state = new RetryState(grainId, reminderName, scheduleId, force, attempt);
        var timer = this.RegisterGrainTimer(
            RetryAsync,
            state,
            new GrainTimerCreationOptions
            {
                DueTime = GetRetryDelay(_options, state.GrainId, state.ReminderName, state.Attempt),
                Period = Timeout.InfiniteTimeSpan,
                KeepAlive = true,
            });
        _retryTimers[reminderName] = new(timer, state);
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
                force: state.Force,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Retrying durable job creation for reminder {ReminderName} on grain {GrainId}.",
                state.ReminderName,
                state.GrainId);
            ScheduleRetry(
                state.GrainId,
                state.ReminderName,
                state.ScheduleId,
                state.Force,
                minimumAttempt: state.Attempt + 1);
        }
    }

    private void ClearRetry(string reminderName)
    {
        if (_retryTimers.Remove(reminderName, out var registration))
        {
            registration.Timer.Dispose();
        }
    }

    internal static TimeSpan GetRetryDelay(ReminderOptions options, GrainId grainId, string reminderName, int attempt)
    {
        var multiplier = Math.Pow(2, Math.Min(attempt, 30));
        var baseTicks = Math.Min(options.SchedulingRetryMaxDelay.Ticks, options.SchedulingRetryInitialDelay.Ticks * multiplier);
        var hash = HashCode.Combine(grainId.GetHashCode(), StringComparer.Ordinal.GetHashCode(reminderName), attempt);
        var jitter = 1d + ((uint)hash / (double)uint.MaxValue * 0.2d);
        var ticks = Math.Min(options.SchedulingRetryMaxDelay.Ticks, Math.Max(1, baseTicks * jitter));
        return TimeSpan.FromTicks((long)ticks);
    }

    private sealed record RetryState(GrainId GrainId, string ReminderName, string? ScheduleId, bool Force, int Attempt);

    private sealed record RetryRegistration(IGrainTimer Timer, RetryState State);
}
