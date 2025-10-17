using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.ScheduledJobs;

public class InMemoryJobQueue : IAsyncEnumerable<IScheduledJobContext>
{
    private readonly PriorityQueue<JobBucket, DateTimeOffset> _queue = new();
    private readonly Dictionary<string, JobBucket> _jobsIdToBucket = new();
    private readonly Dictionary<DateTimeOffset, JobBucket> _buckets = new();
    private bool _isFrozen;
    private object _syncLock = new();

    public int Count => _queue.Count;

    public void Enqueue(IScheduledJob job, int dequeueCount)
    {
        lock (_syncLock)
        {
            if (_isFrozen)
                throw new InvalidOperationException("Cannot enqueue job to a frozen queue.");

            var bucket = GetJobBucket(job.DueTime);
            bucket.AddJob(job, dequeueCount);
            _jobsIdToBucket[job.Id] = bucket;
        }
    }

    public void MarkAsFrozen()
    {
        lock (_syncLock)
        {
            _isFrozen = true;
        }
    }

    public void CancelJob(string jobId)
    {
        lock (_syncLock)
        {
            if (_jobsIdToBucket.TryGetValue(jobId, out var bucket))
            {
                bucket.RemoveJob(jobId);
                _jobsIdToBucket.Remove(jobId);
                // Note: The bucket remains in the priority queue until processed
            }
        }
    }

    public void RetryJobLater(IScheduledJobContext jobContext, DateTimeOffset newDueTime)
    {
        var jobId = jobContext.Job.Id;
        lock (_syncLock)
        {
            if (_jobsIdToBucket.TryGetValue(jobId, out var oldBucket))
            {
                oldBucket.RemoveJob(jobId);
                _jobsIdToBucket.Remove(jobId);
                var newBucket = GetJobBucket(newDueTime);
                newBucket.AddJob(jobContext.Job, jobContext.DequeueCount);
            }
        }
    }

    public async IAsyncEnumerator<IScheduledJobContext> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (true)
        {
            IScheduledJob? job = null;
            int dequeueCount = 0;
            lock (_syncLock)
            {
                if (_queue.Count == 0)
                {
                    if (_isFrozen)
                    {
                        yield break; // Exit if the queue is frozen and empty
                    }
                }
                else
                {
                    var nextBucket = _queue.Peek();
                    if (nextBucket.DueTime < DateTimeOffset.Now)
                    {
                        if (nextBucket.Count == 0)
                        {
                            _queue.Dequeue(); // Remove empty bucket
                        }
                        else
                        {
                            (job, dequeueCount) = nextBucket.Jobs.First();
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
    private readonly Dictionary<string, (IScheduledJob Job, int DequeueCount)> _jobs = new();

    public int Count => _jobs.Count;

    public DateTimeOffset DueTime { get; private set; }

    public IEnumerable<(IScheduledJob Job, int DequeueCount)> Jobs => _jobs.Values;

    public (IScheduledJob Job, int DequeueCount) this[string jobId] => _jobs[jobId];

    public JobBucket(DateTimeOffset dueTime)
    {
        DueTime = dueTime;
    }

    public void AddJob(IScheduledJob job, int dequeueCount)
    {
        _jobs[job.Id] = (job, dequeueCount);
    }

    public bool RemoveJob(string jobId)
    {
        return _jobs.Remove(jobId);
    }
}
