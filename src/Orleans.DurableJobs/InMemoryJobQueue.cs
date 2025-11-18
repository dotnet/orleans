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
internal sealed class InMemoryJobQueue : IAsyncEnumerable<IDurableJobContext>
{
    private readonly PriorityQueue<JobBucket, DateTimeOffset> _queue = new();
    private readonly Dictionary<string, JobBucket> _jobsIdToBucket = new();
    private readonly Dictionary<DateTimeOffset, JobBucket> _buckets = new();
    private bool _isComplete;
    private readonly object _syncLock = new();

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

        lock (_syncLock)
        {
            if (_isComplete)
                throw new InvalidOperationException("Cannot enqueue job to a completed queue.");

            var bucket = GetJobBucket(job.DueTime);
            bucket.AddJob(job, dequeueCount);
            _jobsIdToBucket[job.Id] = bucket;
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
    public void RetryJobLater(IDurableJobContext jobContext, DateTimeOffset newDueTime)
    {
        var jobId = jobContext.Job.Id;
        var newJob = new DurableJob
        {
            Id = jobContext.Job.Id,
            Name = jobContext.Job.Name,
            DueTime = newDueTime,
            TargetGrainId = jobContext.Job.TargetGrainId,
            ShardId = jobContext.Job.ShardId,
            Metadata = jobContext.Job.Metadata
        };

        lock (_syncLock)
        {
            if (_jobsIdToBucket.TryGetValue(jobId, out var oldBucket))
            {
                oldBucket.RemoveJob(jobId);
                _jobsIdToBucket.Remove(jobId);
                var newBucket = GetJobBucket(newDueTime);
                newBucket.AddJob(newJob, jobContext.DequeueCount);
                _jobsIdToBucket[jobId] = newBucket;
            }
        }
    }

    /// <summary>
    /// Returns an asynchronous enumerator that yields durable jobs as they become due.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// An async enumerator that returns <see cref="IDurableJobContext"/> instances for jobs that are due.
    /// The enumerator checks for due jobs every second and terminates when the queue is marked complete and empty.
    /// </returns>
    public async IAsyncEnumerator<IDurableJobContext> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (true)
        {
            JobBucket? bucketToProcess = null;
            DateTimeOffset bucketKey = default;

            lock (_syncLock)
            {
                if (Count == 0)
                {
                    if (_isComplete)
                    {
                        yield break; // Exit if the queue is frozen and empty
                    }
                }
                else if (_queue.Count > 0)
                {
                    var nextBucket = _queue.Peek();
                    if (nextBucket.DueTime < DateTimeOffset.UtcNow)
                    {
                        // Dequeue the entire bucket to process outside the lock
                        bucketToProcess = _queue.Dequeue();
                        bucketKey = bucketToProcess.DueTime;
                    }
                }
            }

            if (bucketToProcess is not null)
            {
                // Process all jobs in the bucket outside the lock for better concurrency
                foreach (var (job, dequeueCount) in bucketToProcess.Jobs.ToList())
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
                        yield return new DurableJobContext(job, Guid.NewGuid().ToString(), dequeueCount + 1);
                    }
                }

                // Clean up the bucket from dictionary after processing all jobs
                lock (_syncLock)
                {
                    _buckets.Remove(bucketKey);
                }
            }
            else
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
        }
    }

    private JobBucket GetJobBucket(DateTimeOffset dueTime)
    {
        // Truncate to second precision and add 1 second to normalize bucket key
        // This ensures all jobs within the same second (e.g., 12:00:00.000-12:00:00.999) share the same bucket (12:00:01)
        var key = new DateTimeOffset(dueTime.Year, dueTime.Month, dueTime.Day, dueTime.Hour, dueTime.Minute, dueTime.Second, dueTime.Offset);
        key = key.AddSeconds(1);
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new JobBucket(key);
            _buckets[key] = bucket;
            _queue.Enqueue(bucket, key);
        }
        return bucket;
    }   
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
}
