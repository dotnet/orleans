using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableJobs;

/// <summary>
/// Provides an in-memory priority queue for managing durable jobs based on their due times.
/// Jobs are organized into time-based buckets and enumerated asynchronously as they become due.
/// </summary>
internal sealed class InMemoryJobQueue : IAsyncEnumerable<IJobRunContext>
{
    private readonly TimeProvider _timeProvider;
    private readonly PriorityQueue<JobBucket, DateTimeOffset> _queue = new();
    private readonly Dictionary<string, JobBucket> _jobsIdToBucket = new();
    private readonly Dictionary<DateTimeOffset, JobBucket> _buckets = new();
    private TaskCompletionSource _queueChanged = CreateQueueChangedSource();
    private bool _isComplete;
#if NET9_0_OR_GREATER
    private readonly Lock _syncLock = new();
#else
    private readonly object _syncLock = new();
#endif

    public InMemoryJobQueue(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the total number of jobs currently in the queue.
    /// </summary>
    public int Count => _jobsIdToBucket.Count;

    /// <summary>
    /// Adds a durable job to the queue with the specified dequeue count.
    /// </summary>
    /// <param name="job">The durable job to enqueue.</param>
    /// <param name="dequeueCount">The number of times this job has been dequeued previously.</param>
    /// <exception cref="InvalidOperationException">Thrown when attempting to enqueue a job to a completed queue.</exception>
    /// <exception cref="ArgumentNullException">Thrown when job is null.</exception>
    public void Enqueue(DurableJob job, int dequeueCount)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (dequeueCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dequeueCount));
        }

        lock (_syncLock)
        {
            if (_isComplete)
                throw new InvalidOperationException("Cannot enqueue job to a completed queue.");

            var bucket = GetJobBucket(job.DueTime);
            bucket.AddJob(job, dequeueCount);
            _jobsIdToBucket[job.Id] = bucket;
            SignalQueueChanged();
        }
    }

    /// <summary>
    /// Marks the queue as complete, preventing any further jobs from being enqueued.
    /// Once marked complete, the queue will finish processing remaining jobs and then terminate enumeration.
    /// </summary>
    public void MarkAsComplete()
    {
        lock (_syncLock)
        {
            _isComplete = true;
            SignalQueueChanged();
        }
    }

    /// <summary>
    /// Cancels a durable job by removing it from the queue.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to cancel.</param>
    /// <returns>True if the job was found and removed; false if the job was not found.</returns>
    /// <remarks>
    /// The job's bucket remains in the priority queue until processed, but the job itself is removed immediately.
    /// </remarks>
    public bool CancelJob(string jobId)
    {
        lock (_syncLock)
        {
            if (_jobsIdToBucket.TryGetValue(jobId, out var bucket))
            {
                // Try to remove from bucket (may already be dequeued)
                bucket.RemoveJob(jobId);
                _jobsIdToBucket.Remove(jobId);
                // Note: The bucket remains in the priority queue until processed
                SignalQueueChanged();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Reschedules a job for retry with a new due time.
    /// </summary>
    /// <param name="jobContext">The context of the job to retry.</param>
    /// <param name="newDueTime">The new due time for the job.</param>
    /// <remarks>
    /// The job is removed from its current bucket and added to a new bucket based on the specified due time.
    /// The dequeue count from the context is preserved.
    /// </remarks>
    public void RetryJobLater(IJobRunContext jobContext, DateTimeOffset newDueTime)
    {
        ArgumentNullException.ThrowIfNull(jobContext);
        _ = RetryJobLater(jobContext.Job.Id, newDueTime, jobContext.DequeueCount);
    }

    /// <summary>
    /// Reschedules a job for retry with a new due time.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to retry.</param>
    /// <param name="newDueTime">The new due time for the job.</param>
    /// <param name="dequeueCount">The persisted dequeue count to associate with the retried job.</param>
    /// <returns>True if the job was found and rescheduled; false if the job was not found.</returns>
    public bool RetryJobLater(string jobId, DateTimeOffset newDueTime, int dequeueCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (dequeueCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dequeueCount));
        }

        lock (_syncLock)
        {
            if (!_jobsIdToBucket.TryGetValue(jobId, out var oldBucket) || !oldBucket.TryGetJob(jobId, out var existing))
            {
                return false;
            }

            var newJob = new DurableJob
            {
                Id = existing.Job.Id,
                Name = existing.Job.Name,
                DueTime = newDueTime,
                TargetGrainId = existing.Job.TargetGrainId,
                ShardId = existing.Job.ShardId,
                Metadata = existing.Job.Metadata
            };

            oldBucket.RemoveJob(jobId);
            _jobsIdToBucket.Remove(jobId);
            var newBucket = GetJobBucket(newDueTime);
            newBucket.AddJob(newJob, dequeueCount);
            _jobsIdToBucket[jobId] = newBucket;
            SignalQueueChanged();
            return true;
        }
    }

    /// <summary>
    /// Gets a point-in-time snapshot of live jobs and their persisted dequeue counts.
    /// </summary>
    /// <returns>The current live jobs and dequeue counts.</returns>
    public IReadOnlyList<(DurableJob Job, int DequeueCount)> GetSnapshot()
    {
        lock (_syncLock)
        {
            var result = new List<(DurableJob Job, int DequeueCount)>(_jobsIdToBucket.Count);
            foreach (var (jobId, bucket) in _jobsIdToBucket)
            {
                if (bucket.TryGetJob(jobId, out var item))
                {
                    result.Add(item);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Clears all queue state.
    /// </summary>
    public void Clear()
    {
        lock (_syncLock)
        {
            _queue.Clear();
            _jobsIdToBucket.Clear();
            _buckets.Clear();
            _isComplete = false;
            SignalQueueChanged();
        }
    }

    /// <summary>
    /// Returns an asynchronous enumerator that yields durable jobs as they become due.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// An async enumerator that returns <see cref="IJobRunContext"/> instances for jobs that are due.
    /// The enumerator wakes when the queue changes or the next job becomes due, and terminates when the queue is marked complete and empty.
    /// </returns>
    public async IAsyncEnumerator<IJobRunContext> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            List<(DurableJob Job, int DequeueCount)>? jobsToYield = null;
            Task? queueChanged = null;
            TimeSpan? delay = null;

            lock (_syncLock)
            {
                RemoveEmptyBuckets();

                if (Count == 0)
                {
                    if (_isComplete)
                    {
                        yield break; // Exit if the queue is frozen and empty
                    }

                    queueChanged = _queueChanged.Task;
                }
                else if (_queue.Count > 0)
                {
                    var nextBucket = _queue.Peek();
                    var now = _timeProvider.GetUtcNow();
                    if (nextBucket.DueTime <= now)
                    {
                        // Dequeue the bucket and remove it from _buckets atomically so a concurrent
                        // Enqueue for the same DueTime cannot reuse this bucket. Without this,
                        // GetJobBucket would find the bucket still in _buckets and add to it,
                        // but the bucket is no longer in _queue, so the new job would be stranded.
                        var bucketToProcess = _queue.Dequeue();
                        _buckets.Remove(bucketToProcess.DueTime);

                        // Snapshot the jobs under the lock so concurrent Cancel/Retry mutations
                        // do not race the enumeration.
                        jobsToYield = new List<(DurableJob Job, int DequeueCount)>(bucketToProcess.Jobs);
                    }
                    else
                    {
                        queueChanged = _queueChanged.Task;
                        delay = nextBucket.DueTime - now;
                    }
                }
                else
                {
                    queueChanged = _queueChanged.Task;
                }
            }

            if (jobsToYield is not null)
            {
                // Process all jobs in the bucket outside the lock for better concurrency
                foreach (var (job, dequeueCount) in jobsToYield)
                {
                    // Verify job hasn't been cancelled while we were processing
                    bool shouldYield;
                    lock (_syncLock)
                    {
                        shouldYield = _jobsIdToBucket.ContainsKey(job.Id);
                        // Keep job in _jobsIdToBucket for explicit removal via CancelJob/RetryJobLater
                    }

                    if (shouldYield)
                    {
                        yield return new JobRunContext(job, Guid.NewGuid().ToString(), dequeueCount + 1);
                    }
                }
            }
            else
            {
                await WaitForQueueChangeOrDelayAsync(queueChanged!, delay, cancellationToken);
            }
        }
    }

    private JobBucket GetJobBucket(DateTimeOffset dueTime)
    {
        if (!_buckets.TryGetValue(dueTime, out var bucket))
        {
            bucket = new JobBucket(dueTime);
            _buckets[dueTime] = bucket;
            _queue.Enqueue(bucket, dueTime);
        }

        return bucket;
    }

    private void RemoveEmptyBuckets()
    {
        while (_queue.Count > 0 && _queue.Peek().Count == 0)
        {
            var bucket = _queue.Dequeue();
            _buckets.Remove(bucket.DueTime);
        }
    }

    private void SignalQueueChanged()
    {
        var previous = _queueChanged;
        _queueChanged = CreateQueueChangedSource();
        previous.TrySetResult();
    }

    private async Task WaitForQueueChangeOrDelayAsync(Task queueChanged, TimeSpan? delay, CancellationToken cancellationToken)
    {
        if (delay is null)
        {
            await queueChanged.WaitAsync(cancellationToken);
            return;
        }

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queueChangedTask = queueChanged.WaitAsync(waitCancellation.Token);
        var delayTask = Task.Delay(delay.Value, _timeProvider, waitCancellation.Token);
        var completedTask = await Task.WhenAny(queueChangedTask, delayTask);
        waitCancellation.Cancel();
        await completedTask;
    }

    private static TaskCompletionSource CreateQueueChangedSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class JobBucket
{
    private readonly Dictionary<string, (DurableJob Job, int DequeueCount)> _jobs = new();

    public int Count => _jobs.Count;

    public DateTimeOffset DueTime { get; private set; }

    public IEnumerable<(DurableJob Job, int DequeueCount)> Jobs => _jobs.Values;

    public JobBucket(DateTimeOffset dueTime)
    {
        DueTime = dueTime;
    }

    public void AddJob(DurableJob job, int dequeueCount)
    {
        _jobs[job.Id] = (job, dequeueCount);
    }

    public bool RemoveJob(string jobId)
    {
        return _jobs.Remove(jobId);
    }

    public bool TryGetJob(string jobId, out (DurableJob Job, int DequeueCount) job)
    {
        return _jobs.TryGetValue(jobId, out job);
    }
}
