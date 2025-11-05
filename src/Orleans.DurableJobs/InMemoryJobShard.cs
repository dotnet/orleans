using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

[DebuggerDisplay("ShardId={Id}, StartTime={StartTime}, EndTime={EndTime}")]
internal sealed class InMemoryJobShard : JobShard
{
    public InMemoryJobShard(string shardId, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string>? metadata)
        : base(shardId, minDueTime, maxDueTime)
    {
        Metadata = metadata;
    }

    protected override Task PersistAddJobAsync(string jobId, string jobName, DateTimeOffset dueTime, GrainId target, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override Task PersistRetryJobAsync(string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
