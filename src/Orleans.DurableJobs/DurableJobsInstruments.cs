using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

internal sealed class DurableJobsInstruments(OrleansInstruments instruments)
{
    private const string MillisecondsUnit = "ms";
    private const string BytesUnit = "bytes";
    private const string StatusTagName = "status";
    private const string StripeTagName = "stripe";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";
    private const string StatusRetried = "retried";
    private const string StatusCanceled = "canceled";
    private const string StatusError = "error";
    private const string StatusOk = "ok";
    private const string StatusNotFound = "not_found";

    internal static DurableJobsInstruments CreateForDirectConstruction()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<DurableJobsInstruments>();
        return services.BuildServiceProvider().GetRequiredService<DurableJobsInstruments>();
    }

    private readonly Counter<long> JobsScheduled = instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-scheduled");
    private readonly Counter<long> JobAttemptsStarted = instruments.Meter.CreateCounter<long>("orleans-durablejobs-job-attempts-started");
    private readonly Counter<long> JobsCompleted = instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-completed");
    private readonly Counter<long> JobsFailed = instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-failed");
    private readonly Counter<long> JobsRetried = instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-retried");
    private readonly Counter<long> JobsCanceled = instruments.Meter.CreateCounter<long>("orleans-durablejobs-jobs-canceled");
    private readonly Counter<long> ShardsProcessed = instruments.Meter.CreateCounter<long>("orleans-durablejobs-shards-processed");
    private readonly Counter<long> ScheduleJobCalls = instruments.Meter.CreateCounter<long>("orleans-durablejobs-schedule-job-calls");
    private readonly Counter<long> CancelJobCalls = instruments.Meter.CreateCounter<long>("orleans-durablejobs-cancel-job-calls");
    private readonly Counter<long> HandlerExecutionsStarted = instruments.Meter.CreateCounter<long>("orleans-durablejobs-handler-executions-started");
    private readonly Counter<long> HandlerExecutions = instruments.Meter.CreateCounter<long>("orleans-durablejobs-handler-executions");
    private readonly Counter<long> StorageBatches = instruments.Meter.CreateCounter<long>("orleans-durablejobs-storage-batches");
    private readonly Counter<long> StripeDistribution = instruments.Meter.CreateCounter<long>("orleans-durablejobs-stripe-distribution");

    private readonly Histogram<double> JobScheduleDuration = instruments.Meter.CreateHistogram<double>("orleans-durablejobs-job-schedule-duration", MillisecondsUnit);
    private readonly Histogram<double> JobDispatchLag = instruments.Meter.CreateHistogram<double>("orleans-durablejobs-job-dispatch-lag", MillisecondsUnit);
    private readonly Histogram<double> JobAttemptDuration = instruments.Meter.CreateHistogram<double>("orleans-durablejobs-job-attempt-duration", MillisecondsUnit);
    private readonly Histogram<double> ShardProcessingDuration = instruments.Meter.CreateHistogram<double>("orleans-durablejobs-shard-processing-duration", MillisecondsUnit);
    private readonly Histogram<double> ScheduleJobCallDuration = instruments.Meter.CreateHistogram<double>("orleans-durablejobs-schedule-job-call-duration", MillisecondsUnit);
    private readonly Histogram<double> CancelJobCallDuration = instruments.Meter.CreateHistogram<double>("orleans-durablejobs-cancel-job-call-duration", MillisecondsUnit);
    private readonly Histogram<double> HandlerExecutionDuration = instruments.Meter.CreateHistogram<double>("orleans-durablejobs-handler-execution-duration", MillisecondsUnit);
    private readonly Histogram<long> StorageBatchSize = instruments.Meter.CreateHistogram<long>("orleans-durablejobs-storage-batch-size");
    private readonly Histogram<long> ShardBatchMutations = instruments.Meter.CreateHistogram<long>("orleans-durablejobs-shard-batch-mutations");
    private readonly Histogram<long> ShardBatchBytes = instruments.Meter.CreateHistogram<long>("orleans-durablejobs-shard-batch-bytes", BytesUnit);
    private readonly Histogram<long> ShardPendingDepth = instruments.Meter.CreateHistogram<long>("orleans-durablejobs-shard-pending-depth");
    private readonly Histogram<double> OwnershipCheckDuration = instruments.Meter.CreateHistogram<double>("orleans-durablejobs-ownership-check-duration", MillisecondsUnit);

    internal void OnJobScheduled(TimeSpan latency)
    {
        JobsScheduled.Add(1);
        Record(JobScheduleDuration, latency);
    }

    internal void OnJobAttemptStarted(TimeSpan dispatchLag)
    {
        JobAttemptsStarted.Add(1);
        Record(JobDispatchLag, dispatchLag);
    }

    internal void OnJobCompleted(TimeSpan latency)
    {
        JobsCompleted.Add(1);
        Record(JobAttemptDuration, latency, StatusCompleted);
    }

    internal void OnJobFailed(TimeSpan latency)
    {
        JobsFailed.Add(1);
        Record(JobAttemptDuration, latency, StatusFailed);
    }

    internal void OnJobRetried(TimeSpan latency)
    {
        JobsRetried.Add(1);
        Record(JobAttemptDuration, latency, StatusRetried);
    }

    internal void OnJobCanceled()
    {
        JobsCanceled.Add(1);
    }

    internal void OnScheduleJobCallSucceeded(TimeSpan latency) => OnScheduleJobCall(latency, StatusOk);

    internal void OnScheduleJobCallCanceled(TimeSpan latency) => OnScheduleJobCall(latency, StatusCanceled);

    internal void OnScheduleJobCallFailed(TimeSpan latency) => OnScheduleJobCall(latency, StatusError);

    internal void OnCancelJobCall(TimeSpan latency, bool canceledJob) => OnCancelJobCall(latency, canceledJob ? StatusOk : StatusNotFound);

    internal void OnCancelJobCallCanceled(TimeSpan latency) => OnCancelJobCall(latency, StatusCanceled);

    internal void OnCancelJobCallFailed(TimeSpan latency) => OnCancelJobCall(latency, StatusError);

    internal void OnHandlerExecutionStarted()
    {
        HandlerExecutionsStarted.Add(1);
    }

    internal void OnHandlerExecutionCompleted(TimeSpan latency) => OnHandlerExecution(latency, StatusCompleted);

    internal void OnHandlerExecutionCanceled(TimeSpan latency) => OnHandlerExecution(latency, StatusCanceled);

    internal void OnHandlerExecutionFailed(TimeSpan latency) => OnHandlerExecution(latency, StatusFailed);

    internal void OnShardProcessed(TimeSpan latency, bool canceled, bool error)
    {
        var status = error ? StatusError : canceled ? StatusCanceled : StatusCompleted;
        ShardsProcessed.Add(1, [new KeyValuePair<string, object?>(StatusTagName, status)]);
        Record(ShardProcessingDuration, latency, status);
    }

    internal void OnStorageBatchWritten(long operationCount, bool canceled, bool error)
    {
        var status = error ? StatusError : canceled ? StatusCanceled : StatusOk;
        var tags = new KeyValuePair<string, object?>[] { new(StatusTagName, status) };
        StorageBatches.Add(1, tags);
        if (StorageBatchSize.Enabled)
        {
            StorageBatchSize.Record(Math.Max(0, operationCount), tags);
        }
    }

    internal void OnShardBatch(long mutationCount)
    {
        if (ShardBatchMutations.Enabled && mutationCount >= 0)
        {
            ShardBatchMutations.Record(mutationCount);
        }
    }

    internal void OnShardBatchBytes(long batchBytes)
    {
        if (ShardBatchBytes.Enabled && batchBytes >= 0)
        {
            ShardBatchBytes.Record(batchBytes);
        }
    }

    internal void OnShardPendingDepth(long depth)
    {
        if (ShardPendingDepth.Enabled && depth >= 0)
        {
            ShardPendingDepth.Record(depth);
        }
    }

    internal void OnOwnershipCheck(TimeSpan duration)
    {
        if (OwnershipCheckDuration.Enabled)
        {
            OwnershipCheckDuration.Record(Math.Max(0, duration.TotalMilliseconds));
        }
    }

    internal void OnStripeAssigned(int stripe)
    {
        if (StripeDistribution.Enabled)
        {
            StripeDistribution.Add(1, [new KeyValuePair<string, object?>(StripeTagName, stripe)]);
        }
    }

    private void OnScheduleJobCall(TimeSpan latency, string status)
    {
        ScheduleJobCalls.Add(1, [new KeyValuePair<string, object?>(StatusTagName, status)]);
        Record(ScheduleJobCallDuration, latency, status);
    }

    private void OnCancelJobCall(TimeSpan latency, string status)
    {
        CancelJobCalls.Add(1, [new KeyValuePair<string, object?>(StatusTagName, status)]);
        Record(CancelJobCallDuration, latency, status);
    }

    private void OnHandlerExecution(TimeSpan latency, string status)
    {
        HandlerExecutions.Add(1, [new KeyValuePair<string, object?>(StatusTagName, status)]);
        Record(HandlerExecutionDuration, latency, status);
    }

    private void Record(Histogram<double> histogram, TimeSpan latency)
    {
        if (histogram.Enabled)
        {
            histogram.Record(Math.Max(0, latency.TotalMilliseconds));
        }
    }

    private void Record(Histogram<double> histogram, TimeSpan latency, string status)
    {
        if (histogram.Enabled)
        {
            histogram.Record(
                Math.Max(0, latency.TotalMilliseconds),
                [new KeyValuePair<string, object?>(StatusTagName, status)]);
        }
    }
}
