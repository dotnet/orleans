using System;
using System.Collections.Generic;
using System.Diagnostics;
using Orleans.Diagnostics;

namespace Orleans.DurableJobs;

/// <summary>
/// Helpers for creating Durable Jobs distributed-tracing activities.
/// </summary>
internal static class DurableJobsDiagnostics
{
    /// <summary>
    /// The activity source used for Durable Jobs spans.
    /// </summary>
    public static ActivitySource Source { get; } = new(ActivitySources.DurableJobsActivitySourceName, "1.0.0");

    public const string ActivityScheduleJob = "schedule durable job";
    public const string ActivityExecuteJob = "execute durable job";
    public const string ActivityExecuteJobHandler = "execute durable job handler";
    public const string ActivityPersistShardBatch = "persist durable job shard batch";
    public const string ActivityDeleteShard = "delete durable job shard";

    private const int MaxBatchLinks = 128;

    /// <summary>
    /// Returns the W3C <c>traceparent</c>/<c>tracestate</c> values for the supplied activity, or both <see langword="null"/> when there is no W3C activity available.
    /// </summary>
    public static (string? TraceParent, string? TraceState) CaptureTraceContext(Activity? activity)
    {
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id is null)
        {
            return (null, null);
        }

        return (activity.Id, activity.TraceStateString);
    }

    /// <summary>
    /// Tries to parse a persisted W3C trace context into an <see cref="ActivityContext"/>.
    /// </summary>
    public static bool TryParseTraceContext(string? traceParent, string? traceState, out ActivityContext context)
    {
        if (string.IsNullOrEmpty(traceParent))
        {
            context = default;
            return false;
        }

        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out context);
    }

    /// <summary>
    /// Starts a Producer activity for a scheduling request and tags it with job attributes once a job id is allocated.
    /// </summary>
    public static Activity? StartScheduleActivity(in ScheduleJobRequest request)
    {
        var activity = Source.StartActivity(ActivityScheduleJob, ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag(ActivityTagKeys.DurableJobName, request.JobName);
            activity.SetTag(ActivityTagKeys.DurableJobTargetGrainId, request.Target.ToString());
            activity.SetTag(ActivityTagKeys.DurableJobDueTime, request.DueTime.ToString("O"));
        }

        return activity;
    }

    /// <summary>
    /// Attaches the persisted/allocated job identity tags to a scheduling activity.
    /// </summary>
    public static void SetScheduledJobTags(Activity? activity, DurableJob job)
    {
        if (activity is null || job is null)
        {
            return;
        }

        activity.SetTag(ActivityTagKeys.DurableJobId, job.Id);
        activity.SetTag(ActivityTagKeys.DurableJobShardId, job.ShardId);
    }

    /// <summary>
    /// Starts a Consumer activity for executing a durable job, optionally continuing the trace recorded when the job was scheduled.
    /// </summary>
    public static Activity? StartExecuteActivity(DurableJob job, int dequeueCount, string? runId)
    {
        ArgumentNullException.ThrowIfNull(job);

        Activity? activity;
        if (TryParseTraceContext(job.TraceParent, job.TraceState, out var parent))
        {
            activity = Source.StartActivity(ActivityExecuteJob, ActivityKind.Consumer, parent);
        }
        else
        {
            activity = Source.StartActivity(ActivityExecuteJob, ActivityKind.Consumer);
        }

        if (activity is not null)
        {
            activity.SetTag(ActivityTagKeys.DurableJobId, job.Id);
            activity.SetTag(ActivityTagKeys.DurableJobName, job.Name);
            activity.SetTag(ActivityTagKeys.DurableJobShardId, job.ShardId);
            activity.SetTag(ActivityTagKeys.DurableJobTargetGrainId, job.TargetGrainId.ToString());
            activity.SetTag(ActivityTagKeys.DurableJobDueTime, job.DueTime.ToString("O"));
            activity.SetTag(ActivityTagKeys.DurableJobDequeueCount, dequeueCount);
            if (runId is not null)
            {
                activity.SetTag(ActivityTagKeys.DurableJobRunId, runId);
            }
        }

        return activity;
    }

    /// <summary>
    /// Starts an internal activity covering the handler invocation portion of a job execution.
    /// </summary>
    public static Activity? StartHandlerActivity(DurableJob job, int dequeueCount, string? runId)
    {
        ArgumentNullException.ThrowIfNull(job);

        var activity = Source.StartActivity(ActivityExecuteJobHandler, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(ActivityTagKeys.DurableJobId, job.Id);
            activity.SetTag(ActivityTagKeys.DurableJobName, job.Name);
            activity.SetTag(ActivityTagKeys.DurableJobShardId, job.ShardId);
            activity.SetTag(ActivityTagKeys.DurableJobDequeueCount, dequeueCount);
            if (runId is not null)
            {
                activity.SetTag(ActivityTagKeys.DurableJobRunId, runId);
            }
        }

        return activity;
    }

    /// <summary>
    /// Records an exception against an activity using the OpenTelemetry semantic-convention tags.
    /// </summary>
    public static void SetError(Activity? activity, Exception exception)
    {
        if (activity is null || exception is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        if (activity.IsAllDataRequested)
        {
            activity.SetTag(ActivityTagKeys.ExceptionType, exception.GetType().FullName);
            activity.SetTag(ActivityTagKeys.ExceptionMessage, exception.Message);
            activity.SetTag(ActivityTagKeys.ExceptionStacktrace, exception.ToString());
            activity.SetTag(ActivityTagKeys.ExceptionEscaped, true);
        }
    }

    /// <summary>
    /// Starts an internal activity that wraps a batched persistence write for a job shard. The first non-default
    /// captured context becomes the parent; subsequent distinct contexts are recorded as <see cref="ActivityLink"/>s
    /// so the resulting span can be reached from every contributing schedule trace.
    /// </summary>
    /// <typeparam name="T">The pending-operation type (must expose a captured <see cref="ActivityContext"/>).</typeparam>
    public static Activity? StartPersistBatchActivity<T>(IReadOnlyList<T> operations, string shardId)
        where T : class
    {
        ActivityContext parent = default;
        List<ActivityLink>? links = null;

        if (operations is not null)
        {
            for (var i = 0; i < operations.Count; i++)
            {
                var context = GetCapturedContext(operations[i]);
                if (context == default)
                {
                    continue;
                }

                if (parent == default)
                {
                    parent = context;
                    continue;
                }

                if (context == parent)
                {
                    continue;
                }

                links ??= new List<ActivityLink>();
                if (links.Count >= MaxBatchLinks)
                {
                    continue;
                }

                var duplicate = false;
                for (var j = 0; j < links.Count; j++)
                {
                    if (links[j].Context == context)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    links.Add(new ActivityLink(context));
                }
            }
        }

        var activity = Source.StartActivity(ActivityPersistShardBatch, ActivityKind.Internal, parent, tags: null, links: links);
        if (activity is not null)
        {
            activity.SetTag(ActivityTagKeys.DurableJobShardId, shardId);
            activity.SetTag(ActivityTagKeys.JournalStorageOperation, "WriteState");
            activity.SetTag(ActivityTagKeys.JournalStorageBatchSize, operations?.Count ?? 0);
        }

        return activity;
    }

    /// <summary>
    /// Starts an internal activity that wraps a shard-state delete. Used so the resulting storage span is part of the
    /// same trace as the originating deletion request.
    /// </summary>
    public static Activity? StartDeleteShardActivity(ActivityContext parent, string shardId)
    {
        var activity = parent == default
            ? Source.StartActivity(ActivityDeleteShard, ActivityKind.Internal)
            : Source.StartActivity(ActivityDeleteShard, ActivityKind.Internal, parent);
        if (activity is not null)
        {
            activity.SetTag(ActivityTagKeys.DurableJobShardId, shardId);
            activity.SetTag(ActivityTagKeys.JournalStorageOperation, "DeleteState");
        }

        return activity;
    }

    private static ActivityContext GetCapturedContext(object operation)
    {
        // PendingOperation lives inside JournaledJobShard. Reach the public CapturedContext property by reflection-free duck-typing
        // via a dedicated interface to keep the helper decoupled from the private type.
        if (operation is IHasCapturedContext capturing)
        {
            return capturing.CapturedContext;
        }

        return default;
    }

    /// <summary>
    /// Implemented by types that carry an <see cref="ActivityContext"/> captured at enqueue time so storage spans can
    /// later be created as children of (or links to) the original activity.
    /// </summary>
    public interface IHasCapturedContext
    {
        ActivityContext CapturedContext { get; }
    }
}
