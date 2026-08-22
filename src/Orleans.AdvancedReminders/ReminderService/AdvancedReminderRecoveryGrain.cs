using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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
    JobShardManager? jobShardManager = null,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider? timeProvider = null) : Grain, IAdvancedReminderRecoveryGrain
{
    private const int BatchSize = 32;
    private const int ScanBucketCount = 4_096;
    internal const int ScanBucketsPerReconciliation = 256;
    private const ulong ScanBucketWidth = (ulong)uint.MaxValue / ScanBucketCount + 1;
    internal static readonly TimeSpan ReconciliationPeriod = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan ReconciliationEntryTimeout = TimeSpan.FromMinutes(1);
    private readonly IReminderTable _reminderTable = reminderTable;
    private readonly IGrainFactory _grainFactory = grainFactory;
    private readonly ILogger<AdvancedReminderRecoveryGrain> _logger = logger;
    private readonly JobShardManager? _jobShardManager = jobShardManager;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private bool _started;
    private bool _forceCurrentScan;
    private int _nextScanBucket;
    private DateTimeOffset _nextReconciliationUtc;

    public async Task StartAsync(bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        if (_started && now < _nextReconciliationUtc)
        {
            return;
        }

        if (force && !_started)
        {
            _forceCurrentScan = true;
        }

        await ReconcileAsync(force: _forceCurrentScan, cancellationToken);
        _started = true;
        _nextReconciliationUtc = _timeProvider.GetUtcNow().Add(ReconciliationPeriod);
    }

    internal async Task ReconcileAsync(bool force, CancellationToken cancellationToken)
    {
        var tasks = new List<Task>(BatchSize);
        var bucketsScanned = 0;
        while (bucketsScanned < ScanBucketsPerReconciliation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucket = _nextScanBucket;
            var begin = bucket == 0 ? uint.MaxValue : (uint)((ulong)bucket * ScanBucketWidth - 1);
            var end = (uint)(((ulong)bucket + 1) * ScanBucketWidth - 1);
            var reminders = (await _reminderTable.ReadRows(begin, end)).Reminders;

            foreach (var entry in reminders)
            {
                var entryForce = force;
                if (!entryForce
                    && !string.IsNullOrEmpty(entry.JobId)
                    && !string.IsNullOrEmpty(entry.JobShardId))
                {
                    if (_jobShardManager is null
                        || await _jobShardManager.ContainsJobAsync(entry.JobShardId, entry.JobId, cancellationToken) is not false)
                    {
                        continue;
                    }

                    entryForce = true;
                }

                tasks.Add(ReconcileEntryAsync(entry, entryForce, cancellationToken));

                if (tasks.Count == BatchSize)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }

            bucketsScanned++;
            _nextScanBucket = (_nextScanBucket + 1) % ScanBucketCount;
            if (_nextScanBucket == 0)
            {
                _forceCurrentScan = false;
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
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
