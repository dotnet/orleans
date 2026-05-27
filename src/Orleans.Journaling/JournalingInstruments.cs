using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal static class JournalingInstruments
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

    private static readonly Counter<long> StateWriteRequests = Instruments.Meter.CreateCounter<long>("orleans-journaling-state-write-requests");
    private static readonly Counter<long> StateDeleteRequests = Instruments.Meter.CreateCounter<long>("orleans-journaling-state-delete-requests");
    private static readonly Counter<long> Recoveries = Instruments.Meter.CreateCounter<long>("orleans-journaling-recoveries");
    private static readonly Counter<long> StorageOperations = Instruments.Meter.CreateCounter<long>("orleans-journaling-storage-operations");
    private static readonly Counter<long> StorageBytes = Instruments.Meter.CreateCounter<long>("orleans-journaling-storage-bytes", BytesUnit);
    private static readonly Counter<long> CompactionTriggers = Instruments.Meter.CreateCounter<long>("orleans-journaling-compaction-triggers");

    private static readonly Histogram<double> StateWriteDuration = Instruments.Meter.CreateHistogram<double>("orleans-journaling-state-write-duration", MillisecondsUnit);
    private static readonly Histogram<double> StateDeleteDuration = Instruments.Meter.CreateHistogram<double>("orleans-journaling-state-delete-duration", MillisecondsUnit);
    private static readonly Histogram<double> RecoveryDuration = Instruments.Meter.CreateHistogram<double>("orleans-journaling-recovery-duration", MillisecondsUnit);
    private static readonly Histogram<double> StorageOperationDuration = Instruments.Meter.CreateHistogram<double>("orleans-journaling-storage-operation-duration", MillisecondsUnit);
    private static readonly Histogram<long> StorageOperationBytes = Instruments.Meter.CreateHistogram<long>("orleans-journaling-storage-operation-bytes", BytesUnit);
    private static readonly Histogram<double> StorageOperationQueueDuration = Instruments.Meter.CreateHistogram<double>("orleans-journaling-storage-operation-queue-duration", MillisecondsUnit);
    private static readonly Histogram<long> WriteCoalescedCallers = Instruments.Meter.CreateHistogram<long>("orleans-journaling-write-coalesced-callers");
    private static readonly Histogram<double> GatherDuration = Instruments.Meter.CreateHistogram<double>("orleans-journaling-gather-duration", MillisecondsUnit);
    private static readonly Histogram<long> StateScanCount = Instruments.Meter.CreateHistogram<long>("orleans-journaling-state-scan-count");

    internal static void OnStateWriteRequest(string operation, TimeSpan latency, bool succeeded)
    {
        Add(StateWriteRequests, operation, succeeded);
        Record(StateWriteDuration, operation, latency, succeeded);
    }

    internal static void OnStateDeleteRequest(TimeSpan latency, bool succeeded)
    {
        Add(StateDeleteRequests, OperationDelete, succeeded);
        Record(StateDeleteDuration, OperationDelete, latency, succeeded);
    }

    internal static void OnRecovery(TimeSpan latency, bool succeeded)
    {
        Add(Recoveries, OperationRecovery, succeeded);
        Record(RecoveryDuration, OperationRecovery, latency, succeeded);
    }

    internal static void OnStorageOperation(string operation, TimeSpan latency, long bytes, bool succeeded)
    {
        Add(StorageOperations, operation, succeeded);
        Record(StorageOperationDuration, operation, latency, succeeded);
        if (succeeded && bytes > 0)
        {
            StorageBytes.Add(bytes, [new KeyValuePair<string, object?>(OperationTagName, operation)]);
            Record(StorageOperationBytes, operation, bytes, succeeded: true);
        }
    }

    internal static void OnStorageOperationQueued(string operation, TimeSpan latency, bool succeeded)
    {
        Record(StorageOperationQueueDuration, operation, latency, succeeded);
    }

    internal static void OnWriteCoalesced(string operation, long callerCount)
    {
        if (WriteCoalescedCallers.Enabled && callerCount > 0)
        {
            WriteCoalescedCallers.Record(callerCount, [new KeyValuePair<string, object?>(OperationTagName, operation)]);
        }
    }

    internal static void OnGather(string operation, TimeSpan duration, long stateScanCount)
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

    internal static void OnCompactionTriggered(string reason)
    {
        if (CompactionTriggers.Enabled)
        {
            CompactionTriggers.Add(1, [new KeyValuePair<string, object?>(ReasonTagName, reason)]);
        }
    }

    private static void Add(Counter<long> counter, string operation, bool succeeded)
    {
        if (counter.Enabled)
        {
            counter.Add(1, CreateTags(operation, succeeded));
        }
    }

    private static void Record(Histogram<double> histogram, string operation, TimeSpan latency, bool succeeded)
    {
        if (histogram.Enabled)
        {
            histogram.Record(Math.Max(0, latency.TotalMilliseconds), CreateTags(operation, succeeded));
        }
    }

    private static void Record(Histogram<long> histogram, string operation, long value, bool succeeded)
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
}
