using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

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
public interface IJobShard : IAsyncDisposable
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
    IAsyncEnumerable<IDurableJobContext> ConsumeDurableJobsAsync();

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
    Task RetryJobLaterAsync(IDurableJobContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to schedule a new job on this shard.
    /// </summary>
    /// <param name="target">The grain identifier of the target grain that will execute the job.</param>
    /// <param name="jobName">The name of the job to schedule.</param>
    /// <param name="dueTime">The time when the job should be executed.</param>
    /// <param name="metadata">Optional metadata to associate with the job.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the durable job if successful, or null if the job could not be scheduled (e.g., the shard was marked as complete).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the due time is outside the shard's time range.</exception>
    Task<DurableJob?> TryScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);
}

/// <summary>
/// Base implementation of <see cref="IJobShard"/> that provides common functionality for job shard implementations.
/// </summary>
public abstract class JobShard : IJobShard
{
    private readonly InMemoryJobQueue _jobQueue;

    /// <inheritdoc/>
    public string Id { get; protected set; }

    /// <inheritdoc/>
    public DateTimeOffset StartTime { get; protected set; }

    /// <inheritdoc/>
    public DateTimeOffset EndTime { get; protected set; }

    /// <inheritdoc/>
    public IDictionary<string, string>? Metadata { get; protected set; }

    /// <inheritdoc/>
    public bool IsAddingCompleted { get; protected set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="JobShard"/> class.
    /// </summary>
    /// <param name="id">The unique identifier for this job shard.</param>
    /// <param name="startTime">The start time of the time range managed by this shard.</param>
    /// <param name="endTime">The end time of the time range managed by this shard.</param>
    protected JobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        Id = id;
        StartTime = startTime;
        EndTime = endTime;
        _jobQueue = new InMemoryJobQueue();
    }

    /// <inheritdoc/>
    public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(_jobQueue.Count);

    /// <inheritdoc/>
    public IAsyncEnumerable<IDurableJobContext> ConsumeDurableJobsAsync()
    {
        return _jobQueue;
    }

    /// <inheritdoc/>
    public async Task<DurableJob?> TryScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        if (IsAddingCompleted)
        {
            return null;
        }

        if (dueTime < StartTime || dueTime > EndTime)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime), "Scheduled time is out of shard bounds.");
        }

        var jobId = Guid.NewGuid().ToString();
        var job = new DurableJob
        {
            Id = jobId,
            TargetGrainId = target,
            Name = jobName,
            DueTime = dueTime,
            ShardId = Id,
            Metadata = metadata
        };

        await PersistAddJobAsync(jobId, jobName, dueTime, target, metadata, cancellationToken);
        _jobQueue.Enqueue(job, 0);
        return job;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await PersistRemoveJobAsync(jobId, cancellationToken);
        return _jobQueue.CancelJob(jobId);
    }

    /// <inheritdoc/>
    public Task MarkAsCompleteAsync(CancellationToken cancellationToken)
    {
        IsAddingCompleted = true;
        _jobQueue.MarkAsComplete();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task RetryJobLaterAsync(IDurableJobContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        await PersistRetryJobAsync(jobContext.Job.Id, newDueTime, cancellationToken);
        _jobQueue.RetryJobLater(jobContext, newDueTime);
    }

    /// <summary>
    /// Enqueues a job into the in-memory queue with the specified dequeue count.
    /// </summary>
    /// <param name="job">The job to enqueue.</param>
    /// <param name="dequeueCount">The number of times this job has been dequeued.</param>
    protected void EnqueueJob(DurableJob job, int dequeueCount)
    {
        _jobQueue.Enqueue(job, dequeueCount);
    }

    /// <summary>
    /// Persists the addition of a new job to the underlying storage.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="jobName">The name of the job.</param>
    /// <param name="dueTime">The time when the job should be executed.</param>
    /// <param name="target">The grain identifier of the target grain.</param>
    /// <param name="metadata">Optional metadata to associate with the job.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    protected abstract Task PersistAddJobAsync(string jobId, string jobName, DateTimeOffset dueTime, GrainId target, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the removal of a job from the underlying storage.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to remove.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    protected abstract Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the rescheduling of a job to the underlying storage.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to retry.</param>
    /// <param name="newDueTime">The new due time for the job.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    protected abstract Task PersistRetryJobAsync(string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}
