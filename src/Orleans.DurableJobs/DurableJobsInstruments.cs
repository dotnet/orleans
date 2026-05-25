using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

internal static class DurableJobsInstruments
{
    private const string MillisecondsUnit = "ms";
    private const string StatusTagName = "status";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";
    private const string StatusRetried = "retried";
    private const string StatusCanceled = "canceled";
    private const string StatusError = "error";
    private const string StatusOk = "ok";
    private const string StatusNotFound = "not_found";

    private static readonly Counter<long> JobsScheduled = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-scheduled");
    private static readonly Counter<long> JobAttemptsStarted = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-job-attempts-started");
    private static readonly Counter<long> JobsCompleted = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-completed");
    private static readonly Counter<long> JobsFailed = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-failed");
    private static readonly Counter<long> JobsRetried = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-retried");
    private static readonly Counter<long> JobsCanceled = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-canceled");
    private static readonly Counter<long> ShardsProcessed = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-shards-processed");
    private static readonly Counter<long> ScheduleJobCalls = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-schedule-job-calls");
    private static readonly Counter<long> CancelJobCalls = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-cancel-job-calls");
    private static readonly Counter<long> HandlerExecutionsStarted = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-handler-executions-started");
    private static readonly Counter<long> HandlerExecutions = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-handler-executions");
    private static readonly Counter<long> StorageBatches = Instruments.Meter.CreateCounter<long>("orleans-durablejobs-storage-batches");

    private static readonly Histogram<double> JobScheduleDuration = Instruments.Meter.CreateHistogram<double>("orleans-durablejobs-job-schedule-duration", MillisecondsUnit);
    private static readonly Histogram<double> JobDispatchLag = Instruments.Meter.CreateHistogram<double>("orleans-durablejobs-job-dispatch-lag", MillisecondsUnit);
    private static readonly Histogram<double> JobAttemptDuration = Instruments.Meter.CreateHistogram<double>("orleans-durablejobs-job-attempt-duration", MillisecondsUnit);
    private static readonly Histogram<double> ShardProcessingDuration = Instruments.Meter.CreateHistogram<double>("orleans-durablejobs-shard-processing-duration", MillisecondsUnit);
    private static readonly Histogram<double> ScheduleJobCallDuration = Instruments.Meter.CreateHistogram<double>("orleans-durablejobs-schedule-job-call-duration", MillisecondsUnit);
    private static readonly Histogram<double> CancelJobCallDuration = Instruments.Meter.CreateHistogram<double>("orleans-durablejobs-cancel-job-call-duration", MillisecondsUnit);
    private static readonly Histogram<double> HandlerExecutionDuration = Instruments.Meter.CreateHistogram<double>("orleans-durablejobs-handler-execution-duration", MillisecondsUnit);
    private static readonly Histogram<long> StorageBatchSize = Instruments.Meter.CreateHistogram<long>("orleans-durablejobs-storage-batch-size");

    internal static void OnJobScheduled(TimeSpan latency)
    {
        JobsScheduled.Add(1);
        Record(JobScheduleDuration, latency);
    }

    internal static void OnJobAttemptStarted(TimeSpan dispatchLag)
    {
        JobAttemptsStarted.Add(1);
        Record(JobDispatchLag, dispatchLag);
    }

    internal static void OnJobCompleted(TimeSpan latency)
    {
        JobsCompleted.Add(1);
        Record(JobAttemptDuration, latency, StatusCompleted);
    }

    internal static void OnJobFailed(TimeSpan latency)
    {
        JobsFailed.Add(1);
        Record(JobAttemptDuration, latency, StatusFailed);
    }

    internal static void OnJobRetried(TimeSpan latency)
    {
        JobsRetried.Add(1);
        Record(JobAttemptDuration, latency, StatusRetried);
    }

    internal static void OnJobCanceled()
    {
        JobsCanceled.Add(1);
    }

    internal static void OnScheduleJobCallSucceeded(TimeSpan latency) => OnScheduleJobCall(latency, StatusOk);

    internal static void OnScheduleJobCallCanceled(TimeSpan latency) => OnScheduleJobCall(latency, StatusCanceled);

    internal static void OnScheduleJobCallFailed(TimeSpan latency) => OnScheduleJobCall(latency, StatusError);

    internal static void OnCancelJobCall(TimeSpan latency, bool canceledJob) => OnCancelJobCall(latency, canceledJob ? StatusOk : StatusNotFound);

    internal static void OnCancelJobCallCanceled(TimeSpan latency) => OnCancelJobCall(latency, StatusCanceled);

    internal static void OnCancelJobCallFailed(TimeSpan latency) => OnCancelJobCall(latency, StatusError);

    internal static void OnHandlerExecutionStarted()
    {
        HandlerExecutionsStarted.Add(1);
    }

    internal static void OnHandlerExecutionCompleted(TimeSpan latency) => OnHandlerExecution(latency, StatusCompleted);

    internal static void OnHandlerExecutionCanceled(TimeSpan latency) => OnHandlerExecution(latency, StatusCanceled);

    internal static void OnHandlerExecutionFailed(TimeSpan latency) => OnHandlerExecution(latency, StatusFailed);

    internal static void OnShardProcessed(TimeSpan latency, bool canceled, bool error)
    {
        var status = error ? StatusError : canceled ? StatusCanceled : StatusCompleted;
        ShardsProcessed.Add(1, [new KeyValuePair<string, object?>(StatusTagName, status)]);
        Record(ShardProcessingDuration, latency, status);
    }

    internal static void OnStorageBatchWritten(long operationCount, bool canceled, bool error)
    {
        var status = error ? StatusError : canceled ? StatusCanceled : StatusOk;
        var tags = new KeyValuePair<string, object?>[] { new(StatusTagName, status) };
        StorageBatches.Add(1, tags);
        if (StorageBatchSize.Enabled)
        {
            StorageBatchSize.Record(Math.Max(0, operationCount), tags);
        }
    }

    private static void OnScheduleJobCall(TimeSpan latency, string status)
    {
        ScheduleJobCalls.Add(1, [new KeyValuePair<string, object?>(StatusTagName, status)]);
        Record(ScheduleJobCallDuration, latency, status);
    }

    private static void OnCancelJobCall(TimeSpan latency, string status)
    {
        CancelJobCalls.Add(1, [new KeyValuePair<string, object?>(StatusTagName, status)]);
        Record(CancelJobCallDuration, latency, status);
    }

    private static void OnHandlerExecution(TimeSpan latency, string status)
    {
        HandlerExecutions.Add(1, [new KeyValuePair<string, object?>(StatusTagName, status)]);
        Record(HandlerExecutionDuration, latency, status);
    }

    private static void Record(Histogram<double> histogram, TimeSpan latency)
    {
        if (histogram.Enabled)
        {
            histogram.Record(Math.Max(0, latency.TotalMilliseconds));
        }
    }

    private static void Record(Histogram<double> histogram, TimeSpan latency, string status)
    {
        if (histogram.Enabled)
        {
            histogram.Record(
                Math.Max(0, latency.TotalMilliseconds),
                [new KeyValuePair<string, object?>(StatusTagName, status)]);
        }
    }
}
