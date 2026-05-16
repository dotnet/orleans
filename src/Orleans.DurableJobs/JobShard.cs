using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableJobs;

/// <summary>
/// Represents an internal shard of durable jobs that manages a collection of jobs within a specific time range.
/// </summary>
internal interface IJobShard : IAsyncDisposable
{
    string Id { get; }

    DateTimeOffset StartTime { get; }

    DateTimeOffset EndTime { get; }

    IDictionary<string, string>? Metadata { get; }

    bool IsAddingCompleted { get; }

    IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync();

    ValueTask<int> GetJobCountAsync();

    Task MarkAsCompleteAsync(CancellationToken cancellationToken);

    Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken);

    Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken);

    Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken);
}
