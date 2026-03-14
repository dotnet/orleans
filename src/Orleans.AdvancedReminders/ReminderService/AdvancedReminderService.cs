using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class AdvancedReminderService : IReminderService, ILifecycleParticipant<ISiloLifecycle>
{
    private const string GrainIdMetadataKey = "grain-id";
    private const string ReminderNameMetadataKey = "reminder-name";
    private const string ETagMetadataKey = "etag";
    private const string JobNamePrefix = "advanced-reminder:";

    private readonly IReminderTable _reminderTable;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly JobShardManager _jobShardManager;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<AdvancedReminderService> _logger;
    private readonly ReminderOptions _options;
    private readonly TimeProvider _timeProvider;

    public AdvancedReminderService(
        IReminderTable reminderTable,
        ILocalDurableJobManager jobManager,
        JobShardManager jobShardManager,
        IGrainFactory grainFactory,
        IOptions<ReminderOptions> options,
        ILogger<AdvancedReminderService> logger,
        TimeProvider timeProvider)
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
            ServiceLifecycleStage.ApplicationServices,
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

        ReminderEntry entry = schedule.Kind switch
        {
            Runtime.ReminderScheduleKind.Interval => CreateIntervalEntry(grainId, reminderName, schedule, priority, action),
            Runtime.ReminderScheduleKind.Cron => CreateCronEntry(grainId, reminderName, schedule, priority, action),
            _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule.Kind, "Unsupported reminder schedule kind."),
        };
        return await UpsertAndScheduleAsync(entry, CancellationToken.None);
    }

    public async Task UnregisterReminder(IGrainReminder reminder)
    {
        if (reminder is not ReminderData data)
        {
            throw new ArgumentException("Reminder handle was not created by Orleans.AdvancedReminders.", nameof(reminder));
        }

        if (await _reminderTable.RemoveRow(data.GrainId, data.ReminderName, data.ETag))
        {
            return;
        }

        var latest = await _reminderTable.ReadRow(data.GrainId, data.ReminderName);
        if (latest is null)
        {
            return;
        }

        if (!await _reminderTable.RemoveRow(data.GrainId, data.ReminderName, latest.ETag))
        {
            throw new Runtime.ReminderException($"Could not unregister reminder {reminder} due to ETag mismatch.");
        }
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

    public async Task ProcessDueReminderAsync(GrainId grainId, string reminderName, string? expectedETag, CancellationToken cancellationToken)
    {
        var entry = await _reminderTable.ReadRow(grainId, reminderName);
        if (entry is null)
        {
            return;
        }

        var currentETag = entry.ETag;
        if (!string.IsNullOrEmpty(expectedETag) && !string.Equals(currentETag, expectedETag, StringComparison.Ordinal))
        {
            return;
        }

        var now = GetUtcNow();
        var due = entry.NextDueUtc ?? entry.StartAt;
        var overdueBy = now > due ? now - due : TimeSpan.Zero;
        var isMissed = overdueBy > _options.MissedReminderGracePeriod;

        var shouldFire = true;
        if (isMissed)
        {
            switch (entry.Action)
            {
                case Runtime.MissedReminderAction.Skip:
                    shouldFire = false;
                    break;
                case Runtime.MissedReminderAction.Notify:
                    shouldFire = false;
                    _logger.LogWarning(
                        "Reminder {ReminderName} for grain {GrainId} missed due window at {Due}. Current time {Now}.",
                        reminderName,
                        grainId,
                        due,
                        now);
                    break;
            }
        }

        if (shouldFire)
        {
            var remindable = _grainFactory.GetGrain<IRemindable>(grainId);
            var status = new Runtime.TickStatus(
                entry.StartAt,
                string.IsNullOrWhiteSpace(entry.CronExpression) ? entry.Period : TimeSpan.Zero,
                now);
            await remindable.ReceiveReminder(entry.ReminderName, status);
            entry.LastFireUtc = now;
        }

        if (!await IsCurrentEntryAsync(entry.GrainId, entry.ReminderName, currentETag))
        {
            return;
        }

        var nextDue = CalculateNextDue(entry, now);
        if (nextDue is null)
        {
            if (!string.IsNullOrEmpty(currentETag))
            {
                await _reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, currentETag);
            }

            return;
        }

        entry.NextDueUtc = nextDue;
        entry.ETag = await _reminderTable.UpsertRow(entry);
        await ScheduleReminderAsync(entry, cancellationToken);
    }

    private async Task<IGrainReminder> UpsertAndScheduleAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        await UpsertAndScheduleEntryAsync(entry, cancellationToken);
        return entry.ToIGrainReminder();
    }

    internal async Task<string> UpsertAndScheduleEntryAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        entry.ETag = await _reminderTable.UpsertRow(entry);
        if (entry.NextDueUtc is not null)
        {
            await ScheduleReminderAsync(entry, cancellationToken);
        }

        return entry.ETag;
    }

    private async Task ScheduleReminderAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        var due = entry.NextDueUtc ?? entry.StartAt;
        var now = GetUtcNow();
        var dueTime = new DateTimeOffset(due <= now ? now.AddMilliseconds(1) : due, TimeSpan.Zero);
        var grainIdText = entry.GrainId.ToString();
        var dispatcher = _grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainIdText);
        await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = dispatcher.GetGrainId(),
                JobName = string.Concat(JobNamePrefix, entry.ReminderName),
                DueTime = dueTime,
                Metadata = new Dictionary<string, string>(capacity: 3, comparer: StringComparer.Ordinal)
                {
                    [GrainIdMetadataKey] = grainIdText,
                    [ReminderNameMetadataKey] = entry.ReminderName,
                    [ETagMetadataKey] = entry.ETag,
                },
            },
            cancellationToken);
    }

    private ReminderEntry CreateIntervalEntry(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.ReminderPriority priority,
        Runtime.MissedReminderAction action)
    {
        if (schedule.Period is not { } period)
        {
            throw new ArgumentException("Interval reminder schedule must define a period.", nameof(schedule));
        }

        var dueAtUtc = schedule.DueAtUtc ?? GetUtcNow().Add(schedule.DueTime ?? throw new ArgumentException("Interval reminder schedule must define dueTime or dueAtUtc.", nameof(schedule)));
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
        var cronExpression = schedule.CronExpression ?? throw new ArgumentException("Cron reminder schedule must define a cron expression.", nameof(schedule));
        var cronSchedule = ReminderCronSchedule.Parse(cronExpression, schedule.CronTimeZoneId);
        var now = GetUtcNow();
        var nextDue = cronSchedule.GetNextOccurrence(now, inclusive: true)
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

    private static DateTime? CalculateNextDue(ReminderEntry entry, DateTime now)
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
            next = next.AddTicks(periodsBehind * entry.Period.Ticks);
        }

        return next;
    }

    private async Task<bool> IsCurrentEntryAsync(GrainId grainId, string reminderName, string? expectedETag)
    {
        if (string.IsNullOrEmpty(expectedETag))
        {
            return true;
        }

        var latest = await _reminderTable.ReadRow(grainId, reminderName);
        return latest is not null && string.Equals(latest.ETag, expectedETag, StringComparison.Ordinal);
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        await _reminderTable.StartAsync(cancellationToken);
        if (!UsesInMemoryDurableJobs())
        {
            return;
        }

        var all = await _reminderTable.ReadRows(0, 0);
        await Parallel.ForEachAsync(
            all.Reminders,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
            },
            async (entry, ct) =>
            {
                if (entry.NextDueUtc is null && entry.Period <= TimeSpan.Zero && string.IsNullOrWhiteSpace(entry.CronExpression))
                {
                    return;
                }

                await ScheduleReminderAsync(entry, ct);
            });
    }

    private Task StopAsync(CancellationToken cancellationToken) => _reminderTable.StopAsync(cancellationToken);

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private bool UsesInMemoryDurableJobs() => string.Equals(_jobShardManager.GetType().Name, "InMemoryJobShardManager", StringComparison.Ordinal);

    internal static bool TryGetReminderMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        out GrainId grainId,
        out string reminderName,
        out string? eTag)
    {
        grainId = default;
        reminderName = string.Empty;
        eTag = null;

        if (metadata is null
            || !metadata.TryGetValue(GrainIdMetadataKey, out var grainIdText)
            || !metadata.TryGetValue(ReminderNameMetadataKey, out var rawReminderName))
        {
            return false;
        }

        reminderName = rawReminderName;
        grainId = GrainId.Parse(grainIdText);
        metadata.TryGetValue(ETagMetadataKey, out eTag);
        return !string.IsNullOrWhiteSpace(reminderName);
    }
}
