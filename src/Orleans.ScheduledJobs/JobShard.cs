using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Utilities;

namespace Orleans.ScheduledJobs;

public abstract class JobShard
{
    // split?

    public string Id { get; protected set; }

    public DateTimeOffset StartTime { get; protected set; }

    public DateTimeOffset EndTime { get; protected set; }

    public IDictionary<string, string>? Metadata { get; protected set; }

    public bool IsComplete { get; protected set; } = false;

    public abstract ValueTask<int> GetJobCount();

    protected JobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        Id = id;
        StartTime = startTime;
        EndTime = endTime;
    }

    // Move to the ShardManager?
    public abstract Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata);

    public abstract IAsyncEnumerable<IScheduledJobContext> ConsumeScheduledJobsAsync();

    public abstract Task RemoveJobAsync(string jobId);

    public abstract Task MarkAsComplete();

    public abstract Task RetryJobLaterAsync(IScheduledJobContext jobContext, DateTimeOffset newDueTime);
}

[DebuggerDisplay("ShardId={Id}, StartTime={StartTime}, EndTime={EndTime}, JobCount={JobCount}")]
internal class InMemoryJobShard : JobShard
{
    private readonly InMemoryJobQueue _jobQueue;

    public InMemoryJobShard(string shardId, DateTimeOffset minDueTime, DateTimeOffset maxDueTime)
        : base(shardId, minDueTime, maxDueTime)
    {
        _jobQueue = new InMemoryJobQueue();
    }

    public override Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata)
    {
        if (IsComplete)
            throw new InvalidOperationException("Cannot schedule job on a complete shard.");

        if (dueTime < StartTime || dueTime > EndTime)
            throw new ArgumentOutOfRangeException(nameof(dueTime), "Scheduled time is out of shard bounds.");

        IScheduledJob job = new ScheduledJob
        {
            Id = Guid.NewGuid().ToString(),
            TargetGrainId = target,
            Name = jobName,
            DueTime = dueTime,
            ShardId = Id,
            Metadata = metadata
        };
        _jobQueue.Enqueue(job, 0);
        return Task.FromResult(job);
    }

    public override IAsyncEnumerable<IScheduledJobContext> ConsumeScheduledJobsAsync() 
    {
        return _jobQueue; 
    }

    public override Task RemoveJobAsync(string jobId)
    {
        _jobQueue.CancelJob(jobId);
        return Task.CompletedTask;
    }

    public override ValueTask<int> GetJobCount() => ValueTask.FromResult(_jobQueue.Count);

    public override Task MarkAsComplete()
    {
        IsComplete = true;
        _jobQueue.MarkAsComplete();
        return Task.CompletedTask;
    }

    public override Task RetryJobLaterAsync(IScheduledJobContext jobContext, DateTimeOffset newDueTime)
    {
        _jobQueue.RetryJobLater(jobContext, newDueTime);
        return Task.CompletedTask;
    }
}
