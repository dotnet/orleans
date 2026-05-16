using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableJobs;

/// <summary>
/// Represents a shard of durable jobs that manages a collection of jobs within a specific time range.
/// A job shard is responsible for storing, retrieving, and managing the lifecycle of durable jobs
/// that fall within its designated time window.
/// </summary>
/// <remarks>
/// Job shards are used to partition durable jobs across time ranges to improve scalability
/// and performance. Each shard has a defined start and end time that determines which jobs
/// it manages. Shards can be marked as complete when all jobs within their time range
/// have been processed.
/// </remarks>
internal interface IJobShard : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this job shard.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the start time of the time range managed by this shard.
    /// </summary>
    DateTimeOffset StartTime { get; }

    /// <summary>
    /// Gets the end time of the time range managed by this shard.
    /// </summary>
    DateTimeOffset EndTime { get; }

    /// <summary>
    /// Gets optional metadata associated with this job shard.
    /// </summary>
    IDictionary<string, string>? Metadata { get; }

    /// <summary>
    /// Gets a value indicating whether this shard has been marked as complete and is no longer accepting new jobs.
    /// </summary>
    /// <remarks>
    /// When a shard is marked as complete (via <see cref="MarkAsCompleteAsync"/>), no new jobs can be added to it.
    /// </remarks>
    bool IsAddingCompleted { get; }

    /// <summary>
    /// Consumes durable jobs from this shard in order of their due time.
    /// </summary>
    /// <returns>An asynchronous enumerable of durable job contexts.</returns>
    IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync();

    /// <summary>
    /// Gets the number of jobs currently scheduled in this shard.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains the job count.</returns>
    ValueTask<int> GetJobCountAsync();

    /// <summary>
    /// Marks this shard as complete, preventing new jobs from being scheduled.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task MarkAsCompleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes a durable job from this shard.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to remove.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains true if the job was successfully removed, or false if the job was not found.</returns>
    Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Reschedules a job to be retried at a later time.
    /// </summary>
    /// <param name="jobContext">The context of the job to retry.</param>
    /// <param name="newDueTime">The new due time for the job.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to schedule a new job on this shard.
    /// </summary>
    /// <param name="request">The request containing the job scheduling parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the durable job if successful, or null if the job could not be scheduled (e.g., the shard was marked as complete).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the due time is outside the shard's time range.</exception>
    Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken);
}
