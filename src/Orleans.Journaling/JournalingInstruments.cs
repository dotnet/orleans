using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class JournalingInstruments(OrleansInstruments instruments)
{
    private const string MillisecondsUnit = "ms";
    private const string BytesUnit = "bytes";
    private const string OperationTagName = "operation";
    private const string StatusTagName = "status";
    private const string ReasonTagName = "reason";
    private const string StatusOk = "ok";
    private const string StatusError = "error";

    internal const string OperationAppend = "append";
    internal const string OperationSnapshot = "snapshot";
    internal const string OperationReplace = "replace";
    internal const string OperationRead = "read";
    internal const string OperationDelete = "delete";
    internal const string OperationRecovery = "recovery";

    internal const string CompactionReasonUserSnapshot = "user_snapshot";
    internal const string CompactionReasonStorageRequested = "storage_requested";
    internal const string CompactionReasonMigration = "migration";

    internal static JournalingInstruments CreateForDirectConstruction() => new(new OrleansInstruments(new DirectMeterFactory()));

    private readonly Counter<long> StateWriteRequests = instruments.Meter.CreateCounter<long>("orleans-journaling-state-write-requests");
    private readonly Counter<long> StateDeleteRequests = instruments.Meter.CreateCounter<long>("orleans-journaling-state-delete-requests");
    private readonly Counter<long> Recoveries = instruments.Meter.CreateCounter<long>("orleans-journaling-recoveries");
    private readonly Counter<long> StorageOperations = instruments.Meter.CreateCounter<long>("orleans-journaling-storage-operations");
    private readonly Counter<long> StorageBytes = instruments.Meter.CreateCounter<long>("orleans-journaling-storage-bytes", BytesUnit);
    private readonly Counter<long> CompactionTriggers = instruments.Meter.CreateCounter<long>("orleans-journaling-compaction-triggers");

    private readonly Histogram<double> StateWriteDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-state-write-duration", MillisecondsUnit);
    private readonly Histogram<double> StateDeleteDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-state-delete-duration", MillisecondsUnit);
    private readonly Histogram<double> RecoveryDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-recovery-duration", MillisecondsUnit);
    private readonly Histogram<double> StorageOperationDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-storage-operation-duration", MillisecondsUnit);
    private readonly Histogram<long> StorageOperationBytes = instruments.Meter.CreateHistogram<long>("orleans-journaling-storage-operation-bytes", BytesUnit);
    private readonly Histogram<double> StorageOperationQueueDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-storage-operation-queue-duration", MillisecondsUnit);
    private readonly Histogram<long> WriteCoalescedCallers = instruments.Meter.CreateHistogram<long>("orleans-journaling-write-coalesced-callers");
    private readonly Histogram<double> GatherDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-gather-duration", MillisecondsUnit);
    private readonly Histogram<long> StateScanCount = instruments.Meter.CreateHistogram<long>("orleans-journaling-state-scan-count");

    internal void OnStateWriteRequest(string operation, TimeSpan latency, bool succeeded)
    {
        Add(StateWriteRequests, operation, succeeded);
        Record(StateWriteDuration, operation, latency, succeeded);
    }

    internal void OnStateDeleteRequest(TimeSpan latency, bool succeeded)
    {
        Add(StateDeleteRequests, OperationDelete, succeeded);
        Record(StateDeleteDuration, OperationDelete, latency, succeeded);
    }

    internal void OnRecovery(TimeSpan latency, bool succeeded)
    {
        Add(Recoveries, OperationRecovery, succeeded);
        Record(RecoveryDuration, OperationRecovery, latency, succeeded);
    }

    internal void OnStorageOperation(string operation, TimeSpan latency, long bytes, bool succeeded)
    {
        Add(StorageOperations, operation, succeeded);
        Record(StorageOperationDuration, operation, latency, succeeded);
        if (succeeded && bytes > 0)
        {
            StorageBytes.Add(bytes, [new KeyValuePair<string, object?>(OperationTagName, operation)]);
            Record(StorageOperationBytes, operation, bytes, succeeded: true);
        }
    }

    internal void OnStorageOperationQueued(string operation, TimeSpan latency, bool succeeded)
    {
        Record(StorageOperationQueueDuration, operation, latency, succeeded);
    }

    internal void OnWriteCoalesced(string operation, long callerCount)
    {
        if (WriteCoalescedCallers.Enabled && callerCount > 0)
        {
            WriteCoalescedCallers.Record(callerCount, [new KeyValuePair<string, object?>(OperationTagName, operation)]);
        }
    }

    internal void OnGather(string operation, TimeSpan duration, long stateScanCount)
    {
        if (GatherDuration.Enabled)
        {
            GatherDuration.Record(Math.Max(0, duration.TotalMilliseconds), [new KeyValuePair<string, object?>(OperationTagName, operation)]);
        }

        if (StateScanCount.Enabled && stateScanCount >= 0)
        {
            StateScanCount.Record(stateScanCount, [new KeyValuePair<string, object?>(OperationTagName, operation)]);
        }
    }

    internal void OnCompactionTriggered(string reason)
    {
        if (CompactionTriggers.Enabled)
        {
            CompactionTriggers.Add(1, [new KeyValuePair<string, object?>(ReasonTagName, reason)]);
        }
    }

    private void Add(Counter<long> counter, string operation, bool succeeded)
    {
        if (counter.Enabled)
        {
            counter.Add(1, CreateTags(operation, succeeded));
        }
    }

    private void Record(Histogram<double> histogram, string operation, TimeSpan latency, bool succeeded)
    {
        if (histogram.Enabled)
        {
            histogram.Record(Math.Max(0, latency.TotalMilliseconds), CreateTags(operation, succeeded));
        }
    }

    private void Record(Histogram<long> histogram, string operation, long value, bool succeeded)
    {
        if (histogram.Enabled)
        {
            histogram.Record(value, CreateTags(operation, succeeded));
        }
    }

    private static KeyValuePair<string, object?>[] CreateTags(string operation, bool succeeded) =>
        [
            new(OperationTagName, operation),
            new(StatusTagName, succeeded ? StatusOk : StatusError)
        ];

    private sealed class DirectMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }
}
