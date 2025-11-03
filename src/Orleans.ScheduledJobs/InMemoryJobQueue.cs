using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.ScheduledJobs;

/// <summary>
/// Provides an in-memory priority queue for managing scheduled jobs based on their due times.
/// Jobs are organized into time-based buckets and enumerated asynchronously as they become due.
/// </summary>
public class InMemoryJobQueue : IAsyncEnumerable<IScheduledJobContext>
{
    private readonly PriorityQueue<JobBucket, DateTimeOffset> _queue = new();
    private readonly Dictionary<string, JobBucket> _jobsIdToBucket = new();
    private readonly Dictionary<DateTimeOffset, JobBucket> _buckets = new();
    private bool _isComplete;
    private object _syncLock = new();

    /// <summary>
    /// Gets the total number of jobs currently in the queue.
    /// </summary>
    public int Count => _jobsIdToBucket.Count;

    /// <summary>
    /// Adds a scheduled job to the queue with the specified dequeue count.
    /// </summary>
    /// <param name="job">The scheduled job to enqueue.</param>
    /// <param name="dequeueCount">The number of times this job has been dequeued previously.</param>
    /// <exception cref="InvalidOperationException">Thrown when attempting to enqueue a job to a completed queue.</exception>
    public void Enqueue(ScheduledJob job, int dequeueCount)
    {
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
    /// Cancels a scheduled job by removing it from the queue.
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
                var removed = bucket.RemoveJob(jobId);
                _jobsIdToBucket.Remove(jobId);
                // Note: The bucket remains in the priority queue until processed
                return removed;
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
    public void RetryJobLater(IScheduledJobContext jobContext, DateTimeOffset newDueTime)
    {
        var jobId = jobContext.Job.Id;
        var newJob = new ScheduledJob
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
    /// Returns an asynchronous enumerator that yields scheduled jobs as they become due.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// An async enumerator that returns <see cref="IScheduledJobContext"/> instances for jobs that are due.
    /// The enumerator checks for due jobs every second and terminates when the queue is marked complete and empty.
    /// </returns>
    public async IAsyncEnumerator<IScheduledJobContext> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (true)
        {
            ScheduledJob? job = null;
            int dequeueCount = 0;
            lock (_syncLock)
            {
                if (_queue.Count == 0)
                {
                    if (_isComplete)
                    {
                        yield break; // Exit if the queue is frozen and empty
                    }
                }
                else
                {
                    var nextBucket = _queue.Peek();
                    if (nextBucket.DueTime < DateTimeOffset.UtcNow)
                    {
                        if (nextBucket.Count == 0)
                        {
                            _queue.Dequeue(); // Remove empty bucket
                            continue;
                        }
                        else
                        {
                            (job, dequeueCount) = nextBucket.Jobs.First();
                            nextBucket.RemoveJob(job.Id);
                        }
                    }
                }
            }
            if (job != null)
            {
                yield return new ScheduledJobContext(job, Guid.NewGuid().ToString(), dequeueCount + 1);
            }
            else
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
        }
    }

    private JobBucket GetJobBucket(DateTimeOffset dueTime)
    {
        var key = new DateTimeOffset(dueTime.Year, dueTime.Month, dueTime.Day, dueTime.Hour, dueTime.Minute, dueTime.Second, dueTime.Offset);
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
    private readonly Dictionary<string, (ScheduledJob Job, int DequeueCount)> _jobs = new();

    public int Count => _jobs.Count;

    public DateTimeOffset DueTime { get; private set; }

    public IEnumerable<(ScheduledJob Job, int DequeueCount)> Jobs => _jobs.Values;

    public (ScheduledJob Job, int DequeueCount) this[string jobId] => _jobs[jobId];

    public JobBucket(DateTimeOffset dueTime)
    {
        DueTime = dueTime;
    }

    public void AddJob(ScheduledJob job, int dequeueCount)
    {
        _jobs[job.Id] = (job, dequeueCount);
    }

    public bool RemoveJob(string jobId)
    {
        return _jobs.Remove(jobId);
    }
}
