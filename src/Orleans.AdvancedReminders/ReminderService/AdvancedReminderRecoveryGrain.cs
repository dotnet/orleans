using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Placement;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAdvancedReminderRecoveryGrain : IGrainWithIntegerKey
{
    Task StartAsync(bool force, CancellationToken cancellationToken);
}

[KeepAlive]
[PreferLocalPlacement]
internal sealed class AdvancedReminderRecoveryGrain(
    IReminderTable reminderTable,
    IGrainFactory grainFactory,
    ILogger<AdvancedReminderRecoveryGrain> logger,
    IOptions<ReminderOptions>? options = null,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider? timeProvider = null) : Grain, IAdvancedReminderRecoveryGrain
{
    private const int BatchSize = 32;
    private const int ScanBucketCount = 256;
    private const ulong ScanBucketWidth = (ulong)uint.MaxValue / ScanBucketCount + 1;
    internal static readonly TimeSpan ReconciliationPeriod = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ReconciliationEntryTimeout = TimeSpan.FromMinutes(1);
    private readonly IReminderTable _reminderTable = reminderTable;
    private readonly IGrainFactory _grainFactory = grainFactory;
    private readonly ILogger<AdvancedReminderRecoveryGrain> _logger = logger;
    private readonly ReminderOptions _options = options?.Value ?? new ReminderOptions();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private bool _started;
    private DateTimeOffset _nextReconciliationUtc;

    public async Task StartAsync(bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        if (_started && now < _nextReconciliationUtc)
        {
            return;
        }

        await ReconcileAsync(force: force && !_started, cancellationToken);
        _started = true;
        _nextReconciliationUtc = _timeProvider.GetUtcNow().Add(ReconciliationPeriod);
    }

    internal async Task ReconcileAsync(bool force, CancellationToken cancellationToken)
    {
        var tasks = new List<Task>(BatchSize);
        for (var bucket = 0; bucket < ScanBucketCount; bucket++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var begin = bucket == 0 ? uint.MaxValue : (uint)((ulong)bucket * ScanBucketWidth - 1);
            var end = (uint)(((ulong)bucket + 1) * ScanBucketWidth - 1);
            var reminders = (await _reminderTable.ReadRows(begin, end)).Reminders;

            foreach (var entry in reminders)
            {
                var entryForce = force || HasStaleJobHandle(entry);
                if (!entryForce && !string.IsNullOrEmpty(entry.JobId) && !string.IsNullOrEmpty(entry.JobShardId))
                {
                    continue;
                }

                tasks.Add(ReconcileEntryAsync(entry, entryForce, cancellationToken));

                if (tasks.Count == BatchSize)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private bool HasStaleJobHandle(ReminderEntry entry)
    {
        if (string.IsNullOrEmpty(entry.JobId) || string.IsNullOrEmpty(entry.JobShardId))
        {
            return false;
        }

        var due = entry.NextDueUtc ?? entry.StartAt;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return due <= now && now - due >= _options.StaleJobRecoveryDelay;
    }

    private async Task ReconcileEntryAsync(ReminderEntry entry, bool force, CancellationToken cancellationToken)
    {
        using var entryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var dispatcher = _grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString());
            await dispatcher.EnsureScheduledAsync(
                    entry.GrainId,
                    entry.ReminderName,
                    entry.ScheduleId,
                    force,
                    entryCts.Token)
                .WaitAsync(ReconciliationEntryTimeout, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            entryCts.Cancel();
            _logger.LogError(
                exception,
                "Error reconciling advanced reminder {ReminderName} for grain {GrainId}.",
                entry.ReminderName,
                entry.GrainId);
        }
    }
}
