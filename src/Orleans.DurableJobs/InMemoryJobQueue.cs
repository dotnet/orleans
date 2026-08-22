using System;
using System.Collections.Generic;
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
    internal const int MaxDequeueBatchSize = 1_024;
    private TaskCompletionSource? _queueChangedWaiter;
    private long _mutationVersion;
    private int _jobCount;
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
    public int Count => Volatile.Read(ref _jobCount);

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

            var wakeCheckRequired = _queueChangedWaiter is not null;
            var previousNextDueTime = wakeCheckRequired ? GetNextReadyDueTime() : null;
            var bucket = GetJobBucket(job.DueTime);
            var isReplacement = _jobsIdToBucket.TryGetValue(job.Id, out var existingBucket);
            if (isReplacement
                && existingBucket is not null
                && !ReferenceEquals(existingBucket, bucket))
            {
                // A replayed or updated job can move to another due-time bucket. Keep a
                // single live bucket membership for each ID so the stale copy cannot be
                // dequeued before the replacement.
                existingBucket.RemoveJob(job.Id);
            }

            bucket.AddJob(job, dequeueCount);
            _jobsIdToBucket[job.Id] = bucket;
            if (!isReplacement)
            {
                Volatile.Write(ref _jobCount, _jobCount + 1);
            }

            PublishMutation();
            if (wakeCheckRequired)
            {
                SignalQueueChangedIfNextDueTimeChanged(previousNextDueTime);
            }
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
            var wakeCheckRequired = _queueChangedWaiter is not null;
            var previousNextDueTime = wakeCheckRequired ? GetNextReadyDueTime() : null;
            if (_jobsIdToBucket.TryGetValue(jobId, out var bucket))
            {
                // Try to remove from bucket (may already be dequeued)
                bucket.RemoveJob(jobId);
                _jobsIdToBucket.Remove(jobId);
                Volatile.Write(ref _jobCount, _jobCount - 1);
                PublishMutation();
                // Note: The bucket remains in the priority queue until processed
                if (wakeCheckRequired)
                {
                    SignalQueueChangedIfNextDueTimeChanged(previousNextDueTime);
                }
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
        _ = RetryJobLater(jobContext.Job.Id, newDueTime, jobContext.DequeueCount, executionGeneration: null);
    }

    /// <summary>
    /// Reschedules a job for retry with a new due time.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to retry.</param>
    /// <param name="newDueTime">The new due time for the job.</param>
    /// <param name="dequeueCount">The persisted dequeue count to associate with the retried job.</param>
    /// <returns>True if the job was found and rescheduled; false if the job was not found.</returns>
    public bool RetryJobLater(string jobId, DateTimeOffset newDueTime, int dequeueCount)
        => RetryJobLater(jobId, newDueTime, dequeueCount, executionGeneration: null);

    internal bool RetryJobLater(string jobId, DateTimeOffset newDueTime, int dequeueCount, long? executionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (dequeueCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dequeueCount));
        }

        lock (_syncLock)
        {
            var wakeCheckRequired = _queueChangedWaiter is not null;
            var previousNextDueTime = wakeCheckRequired ? GetNextReadyDueTime() : null;
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
                Metadata = existing.Job.Metadata,
                TraceParent = existing.Job.TraceParent,
                TraceState = existing.Job.TraceState,
                ExecutionGeneration = executionGeneration ?? existing.Job.ExecutionGeneration,
                Priority = existing.Job.Priority,
            };

            oldBucket.RemoveJob(jobId);
            _jobsIdToBucket.Remove(jobId);
            var newBucket = GetJobBucket(newDueTime);
            newBucket.AddJob(newJob, dequeueCount);
            _jobsIdToBucket[jobId] = newBucket;
            PublishMutation();
            if (wakeCheckRequired)
            {
                SignalQueueChangedIfNextDueTimeChanged(previousNextDueTime);
            }
            return true;
        }
    }

    /// <summary>
    /// Gets a point-in-time snapshot of live jobs and their persisted dequeue counts.
    /// </summary>
    /// <returns>The current live jobs and dequeue counts.</returns>
    public IReadOnlyList<(DurableJob Job, int DequeueCount)> GetSnapshot()
        => GetSnapshot(static (job, dequeueCount) => (job, dequeueCount));

    internal HashSet<string> GetJobIds()
    {
        lock (_syncLock)
        {
            return new HashSet<string>(_jobsIdToBucket.Keys, StringComparer.Ordinal);
        }
    }

    internal List<T> GetSnapshot<T>(Func<DurableJob, int, T> projector)
    {
        ArgumentNullException.ThrowIfNull(projector);
        lock (_syncLock)
        {
            var result = new List<T>(_jobsIdToBucket.Count);
            foreach (var (jobId, bucket) in _jobsIdToBucket)
            {
                if (bucket.TryGetJob(jobId, out var item))
                {
                    result.Add(projector(item.Job, item.DequeueCount));
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
            Volatile.Write(ref _jobCount, 0);
            _isComplete = false;
            PublishMutation();
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
            JobBucket? bucketBeingProcessed = null;
            long batchValidationVersion = 0;
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

                    queueChanged = GetQueueChangedTask();
                }
                else if (_queue.Count > 0)
                {
                    var nextBucket = _queue.Peek();
                    var now = _timeProvider.GetUtcNow();
                    if (nextBucket.DueTime <= now)
                    {
                        bucketBeingProcessed = nextBucket;
                        jobsToYield = nextBucket.TakeReadyJobs(MaxDequeueBatchSize);
                        batchValidationVersion = _mutationVersion;
                        if (nextBucket.ReadyCount == 0)
                        {
                            // Stop accepting new jobs into this bucket after its final ready batch is
                            // detached. Dispatched jobs remain addressable through _jobsIdToBucket until
                            // the executor explicitly removes or retries them.
                            _queue.Dequeue();
                            _buckets.Remove(nextBucket.DueTime);
                        }
                    }
                    else
                    {
                        queueChanged = GetQueueChangedTask();
                        delay = nextBucket.DueTime - now;
                    }
                }
                else
                {
                    queueChanged = GetQueueChangedTask();
                }
            }

            if (jobsToYield is not null)
            {
                for (var index = 0; index < jobsToYield.Count; index++)
                {
                    if (Volatile.Read(ref _mutationVersion) != batchValidationVersion)
                    {
                        // Queue mutations are rare while a detached batch is being yielded. When
                        // one occurs, validate all remaining items in one lock and then stay on the
                        // lock-free path until the version changes again. This preserves the former
                        // per-item cancellation/replacement semantics without taking 1,024 locks for
                        // an uncontended batch.
                        lock (_syncLock)
                        {
                            var writeIndex = index;
                            for (var readIndex = index; readIndex < jobsToYield.Count; readIndex++)
                            {
                                var item = jobsToYield[readIndex];
                                if (_jobsIdToBucket.TryGetValue(item.Job.Id, out var currentBucket)
                                    && ReferenceEquals(currentBucket, bucketBeingProcessed)
                                    && bucketBeingProcessed.ContainsJob(item.Job))
                                {
                                    jobsToYield[writeIndex++] = item;
                                }
                            }

                            if (writeIndex < jobsToYield.Count)
                            {
                                jobsToYield.RemoveRange(writeIndex, jobsToYield.Count - writeIndex);
                            }

                            batchValidationVersion = _mutationVersion;
                        }

                        if (index >= jobsToYield.Count)
                        {
                            break;
                        }
                    }

                    var (job, dequeueCount) = jobsToYield[index];
                    yield return new JobRunContext(job, Guid.NewGuid().ToString("N"), dequeueCount + 1);
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
        while (_queue.Count > 0 && _queue.Peek().ReadyCount == 0)
        {
            var bucket = _queue.Dequeue();
            _buckets.Remove(bucket.DueTime);
        }
    }

    private DateTimeOffset? GetNextReadyDueTime()
    {
        RemoveEmptyBuckets();
        return _queue.Count == 0 ? null : _queue.Peek().DueTime;
    }

    private void SignalQueueChangedIfNextDueTimeChanged(DateTimeOffset? previousNextDueTime)
    {
        if ((_isComplete && _jobCount == 0)
            || previousNextDueTime != GetNextReadyDueTime())
        {
            SignalQueueChanged();
        }
    }

    private void SignalQueueChanged()
    {
        var waiter = _queueChangedWaiter;
        _queueChangedWaiter = null;
        waiter?.TrySetResult();
    }

    private void PublishMutation() => Volatile.Write(ref _mutationVersion, _mutationVersion + 1);

    private Task GetQueueChangedTask()
        => (_queueChangedWaiter ??= CreateQueueChangedSource()).Task;

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
    private readonly Dictionary<string, JobBucketEntry> _jobs = new();
    private JobBucketEntry? _highPriorityHead;
    private JobBucketEntry? _highPriorityTail;
    private JobBucketEntry? _normalPriorityHead;
    private JobBucketEntry? _normalPriorityTail;
    private JobBucketEntry? _lowPriorityHead;
    private JobBucketEntry? _lowPriorityTail;

    public int Count => _jobs.Count;

    public int ReadyCount { get; private set; }

    public DateTimeOffset DueTime { get; private set; }

    public JobBucket(DateTimeOffset dueTime)
    {
        DueTime = dueTime;
    }

    public void AddJob(DurableJob job, int dequeueCount)
    {
        if (_jobs.TryGetValue(job.Id, out var existing))
        {
            RemoveReadyNode(existing);
        }

        var entry = new JobBucketEntry(job, dequeueCount);
        _jobs[job.Id] = entry;
        AppendReady(entry);
        ReadyCount++;
    }

    public bool RemoveJob(string jobId)
    {
        if (!_jobs.Remove(jobId, out var entry))
        {
            return false;
        }

        RemoveReadyNode(entry);
        return true;
    }

    public bool TryGetJob(string jobId, out (DurableJob Job, int DequeueCount) job)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            job = (entry.Job, entry.DequeueCount);
            return true;
        }

        job = default;
        return false;
    }

    public bool ContainsJob(DurableJob job)
        => _jobs.TryGetValue(job.Id, out var entry) && ReferenceEquals(entry.Job, job);

    public List<(DurableJob Job, int DequeueCount)> TakeReadyJobs(int maxCount)
    {
        var result = new List<(DurableJob Job, int DequeueCount)>(Math.Min(maxCount, ReadyCount));
        TakeReadyJobs(ref _highPriorityHead, ref _highPriorityTail, result, maxCount);
        TakeReadyJobs(ref _normalPriorityHead, ref _normalPriorityTail, result, maxCount);
        TakeReadyJobs(ref _lowPriorityHead, ref _lowPriorityTail, result, maxCount);
        return result;
    }

    private void TakeReadyJobs(
        ref JobBucketEntry? head,
        ref JobBucketEntry? tail,
        List<(DurableJob Job, int DequeueCount)> destination,
        int maxCount)
    {
        while (destination.Count < maxCount && head is { } entry)
        {
            head = entry.NextReady;
            if (head is null)
            {
                tail = null;
            }
            else
            {
                head.PreviousReady = null;
            }

            entry.PreviousReady = null;
            entry.NextReady = null;
            entry.IsReady = false;
            ReadyCount--;
            destination.Add((entry.Job, entry.DequeueCount));
        }
    }

    private void AppendReady(JobBucketEntry entry)
    {
        switch (entry.Job.Priority)
        {
            case DurableJobPriority.High:
                AppendReady(ref _highPriorityHead, ref _highPriorityTail, entry);
                break;
            case DurableJobPriority.Low:
                AppendReady(ref _lowPriorityHead, ref _lowPriorityTail, entry);
                break;
            default:
                AppendReady(ref _normalPriorityHead, ref _normalPriorityTail, entry);
                break;
        }
    }

    private static void AppendReady(ref JobBucketEntry? head, ref JobBucketEntry? tail, JobBucketEntry entry)
    {
        entry.IsReady = true;
        entry.PreviousReady = tail;
        if (tail is null)
        {
            head = entry;
        }
        else
        {
            tail.NextReady = entry;
        }

        tail = entry;
    }

    private void RemoveReadyNode(JobBucketEntry entry)
    {
        if (!entry.IsReady)
        {
            return;
        }

        switch (entry.Job.Priority)
        {
            case DurableJobPriority.High:
                RemoveReadyNode(ref _highPriorityHead, ref _highPriorityTail, entry);
                break;
            case DurableJobPriority.Low:
                RemoveReadyNode(ref _lowPriorityHead, ref _lowPriorityTail, entry);
                break;
            default:
                RemoveReadyNode(ref _normalPriorityHead, ref _normalPriorityTail, entry);
                break;
        }

        ReadyCount--;
    }

    private static void RemoveReadyNode(ref JobBucketEntry? head, ref JobBucketEntry? tail, JobBucketEntry entry)
    {
        if (entry.PreviousReady is { } previous)
        {
            previous.NextReady = entry.NextReady;
        }
        else
        {
            head = entry.NextReady;
        }

        if (entry.NextReady is { } next)
        {
            next.PreviousReady = entry.PreviousReady;
        }
        else
        {
            tail = entry.PreviousReady;
        }

        entry.PreviousReady = null;
        entry.NextReady = null;
        entry.IsReady = false;
    }

    private sealed class JobBucketEntry
    {
        public JobBucketEntry(DurableJob job, int dequeueCount)
        {
            Job = job;
            DequeueCount = dequeueCount;
        }

        public DurableJob Job { get; }

        public int DequeueCount { get; }

        public bool IsReady { get; set; }

        public JobBucketEntry? PreviousReady { get; set; }

        public JobBucketEntry? NextReady { get; set; }
    }
}
