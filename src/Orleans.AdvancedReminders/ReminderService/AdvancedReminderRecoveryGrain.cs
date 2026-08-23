using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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
    JobShardManager? jobShardManager = null,
    IOptions<DurableJobsOptions>? durableJobsOptions = null,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider? timeProvider = null) : Grain, IAdvancedReminderRecoveryGrain
{
    private const int BatchSize = 32;
    private const int JobIdCacheCapacity = 32;
    internal const int RecoveryPageSize = 256;
    private const int ScanBucketCount = 4_096;
    internal const int ScanBucketsPerReconciliation = 256;
    private const ulong ScanBucketWidth = (ulong)uint.MaxValue / ScanBucketCount + 1;
    internal static readonly TimeSpan ReconciliationPeriod = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan FullScanPeriod = ReconciliationPeriod * (ScanBucketCount / ScanBucketsPerReconciliation);
    internal static readonly TimeSpan MinimumLookaheadPeriod = FullScanPeriod * 2;
    internal static readonly TimeSpan ReconciliationEntryTimeout = TimeSpan.FromMinutes(1);
    private readonly IReminderTable _reminderTable = reminderTable;
    private readonly IGrainFactory _grainFactory = grainFactory;
    private readonly ILogger<AdvancedReminderRecoveryGrain> _logger = logger;
    private readonly JobShardManager? _jobShardManager = jobShardManager;
    private readonly DurableJobsOptions _durableJobsOptions = durableJobsOptions?.Value ?? new DurableJobsOptions();
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

        var scanStartedUtc = now;
        await ReconcileAsync(force: _forceCurrentScan, cancellationToken);
        _started = true;
        _nextReconciliationUtc = scanStartedUtc.Add(ReconciliationPeriod);
    }

    internal async Task ReconcileAsync(bool force, CancellationToken cancellationToken)
    {
        var tasks = new List<Task>(BatchSize);
        var jobIdsByShard = new BoundedJobIdCache(JobIdCacheCapacity);
        var bucketsScanned = 0;
        while (bucketsScanned < ScanBucketsPerReconciliation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucket = _nextScanBucket;
            var begin = bucket == 0 ? uint.MaxValue : (uint)((ulong)bucket * ScanBucketWidth - 1);
            var end = (uint)(((ulong)bucket + 1) * ScanBucketWidth - 1);
            var lookaheadEnd = _timeProvider.GetUtcNow().UtcDateTime.Add(_durableJobsOptions.ShardLoadLookaheadPeriod);
            string? continuationToken = null;
            do
            {
                var page = await _reminderTable.ReadRows(begin, end, RecoveryPageSize, continuationToken);
                foreach (var entry in page.Reminders)
                {
                    if (!force && (entry.NextDueUtc ?? entry.StartAt) > lookaheadEnd)
                    {
                        continue;
                    }

                    var entryForce = force;
                    if (!entryForce
                        && !string.IsNullOrEmpty(entry.JobId)
                        && !string.IsNullOrEmpty(entry.JobShardId))
                    {
                        if (_jobShardManager is null)
                        {
                            continue;
                        }

                        if (!jobIdsByShard.TryGet(entry.JobShardId, out var existingJobIds))
                        {
                            existingJobIds = await _jobShardManager.GetJobIdsAsync(entry.JobShardId, cancellationToken);
                            jobIdsByShard.Set(entry.JobShardId, existingJobIds);
                        }

                        if (existingJobIds is null || existingJobIds.Contains(entry.JobId))
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

                continuationToken = page.ContinuationToken;
            } while (continuationToken is not null);

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

    private sealed class BoundedJobIdCache(int capacity)
    {
        private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _recency = new();

        public bool TryGet(string shardId, out HashSet<string>? jobIds)
        {
            if (!_entries.TryGetValue(shardId, out var entry))
            {
                jobIds = null;
                return false;
            }

            _recency.Remove(entry.Node);
            _recency.AddLast(entry.Node);
            jobIds = entry.JobIds;
            return true;
        }

        public void Set(string shardId, HashSet<string>? jobIds)
        {
            if (_entries.TryGetValue(shardId, out var existing))
            {
                existing.JobIds = jobIds;
                _recency.Remove(existing.Node);
                _recency.AddLast(existing.Node);
                return;
            }

            if (_entries.Count == capacity)
            {
                var leastRecent = _recency.First!;
                _recency.RemoveFirst();
                _entries.Remove(leastRecent.Value);
            }

            var node = _recency.AddLast(shardId);
            _entries.Add(shardId, new CacheEntry(node, jobIds));
        }

        private sealed class CacheEntry(LinkedListNode<string> node, HashSet<string>? jobIds)
        {
            public LinkedListNode<string> Node { get; } = node;

            public HashSet<string>? JobIds { get; set; } = jobIds;
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
