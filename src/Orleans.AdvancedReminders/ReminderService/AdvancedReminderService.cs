using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class AdvancedReminderService : IReminderService, ILifecycleParticipant<ISiloLifecycle>
{
    internal static readonly TimeSpan RecoveryHeartbeatPeriod = TimeSpan.FromMinutes(1);
    private const string GrainIdMetadataKey = "grain-id";
    private const string ReminderNameMetadataKey = "reminder-name";
    private const string ScheduleIdMetadataKey = "schedule-id";
    private const string LegacyETagMetadataKey = "etag";
    private const string JobNamePrefix = "advanced-reminder:";

    private readonly IReminderTable _reminderTable;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly JobShardManager _jobShardManager;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<AdvancedReminderService> _logger;
    private readonly ReminderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _recoveryMonitorCts = new();
    private Task? _recoveryMonitorTask;

    public AdvancedReminderService(
        IReminderTable reminderTable,
        ILocalDurableJobManager jobManager,
        JobShardManager jobShardManager,
        IGrainFactory grainFactory,
        IOptions<ReminderOptions> options,
        ILogger<AdvancedReminderService> logger,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider timeProvider)
    {
        _reminderTable = reminderTable;
        _jobManager = jobManager;
        _jobShardManager = jobShardManager;
        _grainFactory = grainFactory;
        _logger = logger;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(AdvancedReminderService),
            ServiceLifecycleStage.Active + 1,
            StartAsync,
            StopAsync);
    }

    public async Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ReminderValidation.Validate(_options, reminderName, schedule, priority, action, GetUtcNow());

        ReminderEntry entry = schedule.Kind switch
        {
            Runtime.ReminderScheduleKind.Interval => CreateIntervalEntry(grainId, reminderName, schedule, priority, action),
            Runtime.ReminderScheduleKind.Cron => CreateCronEntry(grainId, reminderName, schedule, priority, action),
            _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule.Kind, "Unsupported reminder schedule kind."),
        };

        return await GetDispatcher(grainId).RegisterOrUpdateAsync(entry);
    }

    public Task UnregisterReminder(IGrainReminder reminder)
    {
        if (reminder is not ReminderData data)
        {
            throw new ArgumentException("Reminder handle was not created by Orleans.AdvancedReminders.", nameof(reminder));
        }

        return GetDispatcher(data.GrainId).UnregisterAsync(data);
    }

    public async Task<IGrainReminder?> GetReminder(GrainId grainId, string reminderName)
        => (await _reminderTable.ReadRow(grainId, reminderName))?.ToIGrainReminder();

    public async Task<List<IGrainReminder>> GetReminders(GrainId grainId)
    {
        var data = await _reminderTable.ReadRows(grainId);
        var result = new List<IGrainReminder>(data.Reminders.Count);
        foreach (var entry in data.Reminders)
        {
            result.Add(entry.ToIGrainReminder());
        }

        return result;
    }

    public Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        CancellationToken cancellationToken)
        => GetDispatcher(grainId).ProcessDueReminderAsync(grainId, reminderName, expectedScheduleId, cancellationToken);

    internal Task<string> UpsertAndScheduleEntryAsync(ReminderEntry entry, CancellationToken cancellationToken)
        => GetDispatcher(entry.GrainId).UpsertAndScheduleAsync(entry, cancellationToken);

    internal async Task<IGrainReminder> RegisterOrUpdateCoreAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        var previous = await _reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        entry.ETag = previous?.ETag ?? string.Empty;
        PrepareNewSchedule(entry);
        await PersistAndScheduleCoreAsync(entry, cancellationToken);
        if (previous is not null)
        {
            await CancelScheduledJobAsync(previous, cancellationToken);
        }

        return entry.ToIGrainReminder();
    }

    internal async Task<string> UpsertAndScheduleCoreAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        var previous = await _reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        if (previous is not null && !string.Equals(previous.ETag, entry.ETag, StringComparison.Ordinal))
        {
            throw new Runtime.ReminderException($"Could not update reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
        }

        PrepareNewSchedule(entry);
        await PersistAndScheduleCoreAsync(entry, cancellationToken);
        if (previous is not null)
        {
            await CancelScheduledJobAsync(previous, cancellationToken);
        }

        return entry.ETag;
    }

    internal async Task UnregisterCoreAsync(ReminderData data, CancellationToken cancellationToken)
    {
        var current = await _reminderTable.ReadRow(data.GrainId, data.ReminderName);
        if (current is null)
        {
            return;
        }

        if (!string.Equals(current.ETag, data.ETag, StringComparison.Ordinal)
            || !await _reminderTable.RemoveRow(data.GrainId, data.ReminderName, data.ETag))
        {
            throw new Runtime.ReminderException($"Could not unregister reminder {data} due to ETag mismatch.");
        }

        await CancelScheduledJobAsync(current, cancellationToken);
    }

    internal async Task ProcessDueReminderCoreAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        CancellationToken cancellationToken)
    {
        var entry = await _reminderTable.ReadRow(grainId, reminderName);
        if (entry is null || !MatchesScheduledOccurrence(entry, expectedScheduleId))
        {
            return;
        }

        var now = GetUtcNow();
        var due = entry.NextDueUtc ?? entry.StartAt;
        if (due > now)
        {
            // A job can become observable before its due time after a clock adjustment.
            // Replace it with a new occurrence instead of firing the reminder early.
            PrepareNewSchedule(entry);
            await PersistAndScheduleCoreAsync(entry, cancellationToken);
            return;
        }

        var overdueBy = now > due ? now - due : TimeSpan.Zero;
        var isMissed = overdueBy > _options.MissedReminderGracePeriod;

        var shouldFire = true;
        if (isMissed && entry.Action != Runtime.MissedReminderAction.FireImmediately)
        {
            shouldFire = false;
            if (entry.Action == Runtime.MissedReminderAction.Notify)
            {
                _logger.LogWarning(
                    "Reminder {ReminderName} for grain {GrainId} missed due window at {Due}. Current time {Now}.",
                    reminderName,
                    grainId,
                    due,
                    now);
            }
        }

        if (shouldFire)
        {
            var remindable = _grainFactory.GetGrain<IRemindable>(grainId);
            var status = new Runtime.TickStatus(
                entry.StartAt,
                string.IsNullOrWhiteSpace(entry.CronExpression) ? entry.Period : TimeSpan.Zero,
                now);
            try
            {
                await remindable.ReceiveReminder(entry.ReminderName, status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Match the classic reminder service: a callback failure is isolated to this
                // tick and must not permanently stop a recurring reminder.
                _logger.LogError(
                    exception,
                    "Error delivering reminder {ReminderName} to grain {GrainId}.",
                    reminderName,
                    grainId);
            }

            entry.LastFireUtc = now;

            // The reminder callback can call back into this dispatcher using the same Orleans call chain.
            // Re-read after the callback so that an unregister or update is not overwritten by this tick.
            var current = await _reminderTable.ReadRow(grainId, reminderName);
            if (current is null
                || !string.Equals(current.ETag, entry.ETag, StringComparison.Ordinal)
                || !string.Equals(current.ScheduleId, entry.ScheduleId, StringComparison.Ordinal))
            {
                return;
            }
        }

        var nextDue = CalculateNextDue(entry, now);
        if (nextDue is null)
        {
            if (!await _reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag))
            {
                throw new Runtime.ReminderException($"Could not remove completed reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
            }

            return;
        }

        entry.NextDueUtc = nextDue;
        PrepareNewSchedule(entry);
        await PersistAndScheduleCoreAsync(entry, cancellationToken);
    }

    internal async Task EnsureScheduledCoreAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken)
    {
        var entry = await _reminderTable.ReadRow(grainId, reminderName);
        if (entry is null || !HasFutureSchedule(entry))
        {
            return;
        }

        if (!string.IsNullOrEmpty(expectedScheduleId)
            && !string.Equals(entry.ScheduleId, expectedScheduleId, StringComparison.Ordinal))
        {
            return;
        }

        if (!force && !string.IsNullOrEmpty(entry.JobId) && !string.IsNullOrEmpty(entry.JobShardId))
        {
            return;
        }

        if (string.IsNullOrEmpty(entry.ScheduleId))
        {
            entry.ScheduleId = Guid.NewGuid().ToString("N");
        }

        entry.JobId = string.Empty;
        entry.JobShardId = string.Empty;
        entry.ETag = await _reminderTable.UpsertRow(entry);
        await ScheduleAndPersistHandleAsync(entry, cancellationToken);
    }

    private async Task PersistAndScheduleCoreAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        entry.ETag = await _reminderTable.UpsertRow(entry);
        if (HasFutureSchedule(entry))
        {
            await ScheduleAndPersistHandleAsync(entry, cancellationToken);
        }
    }

    private async Task ScheduleAndPersistHandleAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        var due = entry.NextDueUtc ?? entry.StartAt;
        var dueTime = new DateTimeOffset(due, TimeSpan.Zero);
        var grainIdText = entry.GrainId.ToString();
        var dispatcher = GetDispatcher(entry.GrainId);
        var job = await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                IdempotencyKey = entry.ScheduleId,
                Target = dispatcher.GetGrainId(),
                JobName = string.Concat(JobNamePrefix, entry.ReminderName),
                DueTime = dueTime,
                Priority = (int)entry.Priority,
                Metadata = new Dictionary<string, string>(capacity: 3, comparer: StringComparer.Ordinal)
                {
                    [GrainIdMetadataKey] = grainIdText,
                    [ReminderNameMetadataKey] = entry.ReminderName,
                    [ScheduleIdMetadataKey] = entry.ScheduleId,
                },
            },
            cancellationToken);

        entry.JobId = job.Id;
        entry.JobShardId = job.ShardId;
        entry.ETag = await _reminderTable.UpsertRow(entry);
    }

    private async Task CancelScheduledJobAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(entry.JobId) || string.IsNullOrEmpty(entry.JobShardId))
        {
            return;
        }

        var canceled = await _jobManager.TryCancelDurableJobAsync(
            new DurableJob
            {
                Id = entry.JobId,
                Name = string.Concat(JobNamePrefix, entry.ReminderName),
                ShardId = entry.JobShardId,
                DueTime = new DateTimeOffset(entry.NextDueUtc ?? entry.StartAt, TimeSpan.Zero),
                TargetGrainId = GetDispatcher(entry.GrainId).GetGrainId(),
            },
            cancellationToken);

        if (!canceled)
        {
            _logger.LogWarning(
                "Durable job {JobId} for reminder {ReminderName} on grain {GrainId} could not be canceled. The stale job will be ignored by its schedule id.",
                entry.JobId,
                entry.ReminderName,
                entry.GrainId);
        }
    }

    private static void PrepareNewSchedule(ReminderEntry entry)
    {
        entry.ScheduleId = Guid.NewGuid().ToString("N");
        entry.JobId = string.Empty;
        entry.JobShardId = string.Empty;
    }

    private ReminderEntry CreateIntervalEntry(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        var period = schedule.Period!.Value;
        var dueAtUtc = schedule.DueAtUtc ?? GetUtcNow().Add(schedule.DueTime!.Value);
        return new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = reminderName,
            StartAt = dueAtUtc,
            Period = period,
            Priority = priority,
            Action = action,
            NextDueUtc = dueAtUtc,
            LastFireUtc = null,
        };
    }

    private ReminderEntry CreateCronEntry(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        var cronSchedule = ReminderCronSchedule.Parse(schedule.CronExpression!, schedule.CronTimeZoneId);
        var nextDue = cronSchedule.GetNextOccurrence(GetUtcNow(), inclusive: true)
            ?? throw new Runtime.ReminderException($"Reminder '{reminderName}' has no future cron occurrences.");

        return new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = reminderName,
            StartAt = nextDue,
            Period = TimeSpan.Zero,
            CronExpression = cronSchedule.Expression.ToExpressionString(),
            CronTimeZoneId = cronSchedule.TimeZoneId ?? string.Empty,
            Priority = priority,
            Action = action,
            NextDueUtc = nextDue,
            LastFireUtc = null,
        };
    }

    internal static DateTime? CalculateNextDue(ReminderEntry entry, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(entry.CronExpression))
        {
            var cronSchedule = ReminderCronSchedule.Parse(entry.CronExpression, entry.CronTimeZoneId);
            return cronSchedule.GetNextOccurrence(now);
        }

        if (entry.Period <= TimeSpan.Zero)
        {
            return null;
        }

        var next = entry.NextDueUtc ?? entry.StartAt;
        if (next <= now)
        {
            var ticksBehind = now.Ticks - next.Ticks;
            var periodsBehind = ticksBehind / entry.Period.Ticks + 1;
            if (periodsBehind > (DateTime.MaxValue.Ticks - next.Ticks) / entry.Period.Ticks)
            {
                return null;
            }

            next = next.AddTicks(periodsBehind * entry.Period.Ticks);
        }

        return next;
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InitializationTimeout);
        try
        {
            await _reminderTable.StartAsync(timeout.Token).WaitAsync(timeout.Token);
            await _grainFactory.GetGrain<IAdvancedReminderRecoveryGrain>(0)
                .StartAsync(force: !_jobShardManager.IsDurableStorage, timeout.Token)
                .WaitAsync(timeout.Token);
            _recoveryMonitorTask ??= MonitorRecoveryAsync(_recoveryMonitorCts.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Advanced reminder initialization exceeded the configured timeout of {_options.InitializationTimeout}.",
                exception);
        }
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        _recoveryMonitorCts.Cancel();
        if (_recoveryMonitorTask is not null)
        {
            try
            {
                await _recoveryMonitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _recoveryMonitorCts.IsCancellationRequested)
            {
            }
        }

        await _reminderTable.StopAsync(cancellationToken);
    }

    private async Task MonitorRecoveryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RecoveryHeartbeatPeriod, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await _grainFactory.GetGrain<IAdvancedReminderRecoveryGrain>(0)
                        .StartAsync(force: false, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error checking advanced reminder recovery service health.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private IAdvancedReminderDispatcherGrain GetDispatcher(GrainId grainId)
        => _grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString());

    private static bool HasFutureSchedule(ReminderEntry entry)
        => entry.NextDueUtc is not null || entry.Period > TimeSpan.Zero || !string.IsNullOrWhiteSpace(entry.CronExpression);

    private static bool MatchesScheduledOccurrence(ReminderEntry entry, string? expectedScheduleId)
        => string.IsNullOrEmpty(expectedScheduleId)
            || string.Equals(entry.ScheduleId, expectedScheduleId, StringComparison.Ordinal)
            || (string.IsNullOrEmpty(entry.ScheduleId) && string.Equals(entry.ETag, expectedScheduleId, StringComparison.Ordinal));

    internal static bool TryGetReminderMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        out GrainId grainId,
        out string reminderName,
        out string? scheduleId)
    {
        grainId = default;
        reminderName = string.Empty;
        scheduleId = null;

        if (metadata is null
            || !metadata.TryGetValue(GrainIdMetadataKey, out var grainIdText)
            || !metadata.TryGetValue(ReminderNameMetadataKey, out var rawReminderName))
        {
            return false;
        }

        reminderName = rawReminderName;
        if (!GrainId.TryParse(grainIdText, out grainId))
        {
            return false;
        }
        if (!metadata.TryGetValue(ScheduleIdMetadataKey, out scheduleId))
        {
            metadata.TryGetValue(LegacyETagMetadataKey, out scheduleId);
        }

        return !string.IsNullOrWhiteSpace(reminderName);
    }
}
