using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class InMemoryJobQueueTests
{
    [Fact]
    public void Enqueue_AddsJobToQueue()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddSeconds(1));

        queue.Enqueue(job, 0);

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Enqueue_MultipleJobs_IncreasesCount()
    {
        var queue = new InMemoryJobQueue();
        var job1 = CreateJob("job1", DateTimeOffset.UtcNow.AddSeconds(1));
        var job2 = CreateJob("job2", DateTimeOffset.UtcNow.AddSeconds(2));
        var job3 = CreateJob("job3", DateTimeOffset.UtcNow.AddSeconds(3));

        queue.Enqueue(job1, 0);
        queue.Enqueue(job2, 0);
        queue.Enqueue(job3, 0);

        Assert.Equal(3, queue.Count);
    }

    [Fact]
    public void Enqueue_AfterMarkAsComplete_ThrowsInvalidOperationException()
    {
        var queue = new InMemoryJobQueue();
        queue.MarkAsComplete();

        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(job, 0));
    }

    [Fact]
    public async Task GetAsyncEnumerator_ReturnsJobsInDueTimeOrder()
    {
        var queue = new InMemoryJobQueue();
        var now = DateTimeOffset.UtcNow;
        var job1 = CreateJob("job1", now.AddMilliseconds(-100));
        var job2 = CreateJob("job2", now.AddMilliseconds(-50));

        queue.Enqueue(job1, 0);
        queue.Enqueue(job2, 0);
        queue.MarkAsComplete();

        var results = new List<IJobRunContext>();
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            results.Add(context);
            if (results.Count >= 2) break;
        }

        Assert.Equal(2, results.Count);
        Assert.Equal("job1", results[0].Job.Name);
        Assert.Equal("job2", results[1].Job.Name);
    }

    [Fact]
    public async Task Enqueue_ReplacingJobInSameBucketPreservesOrderAndCount()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new InMemoryJobQueue();
        var job1 = CreateJob("job1", now.AddSeconds(-1));
        var job2 = CreateJob("job2", job1.DueTime);

        queue.Enqueue(job1, 0);
        queue.Enqueue(job2, 0);
        queue.Enqueue(job1, 7);
        queue.MarkAsComplete();

        Assert.Equal(2, queue.Count);

        await using var enumerator = queue.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(job1.Id, enumerator.Current.Job.Id);
        Assert.Equal(8, enumerator.Current.DequeueCount);
        Assert.True(queue.RemoveJob(job1.Id));

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(job2.Id, enumerator.Current.Job.Id);
        Assert.True(queue.RemoveJob(job2.Id));

        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Enqueue_ReplacingJobInAnotherBucketYieldsOnlyReplacement()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new InMemoryJobQueue();
        var stale = CreateJob("job1", now.AddSeconds(-2));
        var replacement = CreateJob("job1", now.AddSeconds(-1));

        queue.Enqueue(stale, 0);
        queue.Enqueue(replacement, 4);
        queue.MarkAsComplete();

        Assert.Equal(1, queue.Count);

        await using var enumerator = queue.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Same(replacement, enumerator.Current.Job);
        Assert.Equal(5, enumerator.Current.DequeueCount);
        Assert.True(queue.RemoveJob(replacement.Id));

        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task GetAsyncEnumerator_OrdersOneHundredDueTimeBucketsByTimeThenPriority()
    {
        const int bucketCount = 100;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var now = timeProvider.GetUtcNow();
        DurableJobPriority[] enqueueOrder =
        [
            DurableJobPriority.Normal,
            DurableJobPriority.Low,
            DurableJobPriority.High,
        ];

        // Enqueue the newest bucket first and priorities out of order so the test exercises
        // both due-time bucket ordering and priority ordering within every bucket.
        for (var bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
        {
            var dueTime = now.AddMilliseconds(-(bucketIndex + 1));
            foreach (var priority in enqueueOrder)
            {
                var jobName = $"bucket-{bucketIndex:D3}-{priority}";
                queue.Enqueue(CreateJob(jobName, dueTime, priority: priority), 0);
            }
        }

        queue.MarkAsComplete();

        var results = new List<(DateTimeOffset DueTime, DurableJobPriority Priority)>();
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            results.Add((context.Job.DueTime, context.Job.Priority));
            if (results.Count == bucketCount * enqueueOrder.Length)
            {
                break;
            }
        }

        Assert.Equal(bucketCount * enqueueOrder.Length, results.Count);
        for (var resultBucketIndex = 0; resultBucketIndex < bucketCount; resultBucketIndex++)
        {
            var resultOffset = resultBucketIndex * enqueueOrder.Length;
            var expectedDueTime = now.AddMilliseconds(-(bucketCount - resultBucketIndex));
            Assert.All(
                results.GetRange(resultOffset, enqueueOrder.Length),
                result => Assert.Equal(expectedDueTime, result.DueTime));
            Assert.Equal(
                [DurableJobPriority.High, DurableJobPriority.Normal, DurableJobPriority.Low],
                results.Skip(resultOffset).Take(enqueueOrder.Length).Select(static result => result.Priority));
        }
    }

    [Fact]
    public async Task GetAsyncEnumerator_ThirtyThousandJobsAcrossMinuteBuckets_DoesNotLoseDuplicateOrReorderJobs()
    {
        const int bucketCount = 6;
        const int jobsPerBucket = 5_000;
        const int jobCount = bucketCount * jobsPerBucket;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var now = timeProvider.GetUtcNow();

        // Enqueue newest frames first and mix priorities so neither insertion order nor
        // a large same-minute backlog can hide due-time or priority ordering defects.
        for (var bucketIndex = bucketCount - 1; bucketIndex >= 0; bucketIndex--)
        {
            var dueTime = now.AddMinutes(bucketIndex - bucketCount);
            for (var jobIndex = 0; jobIndex < jobsPerBucket; jobIndex++)
            {
                var priority = (DurableJobPriority)((jobIndex % 3) - 1);
                var id = $"bucket-{bucketIndex:D2}-job-{jobIndex:D4}";
                queue.Enqueue(CreateJob(id, dueTime, priority: priority), 0);
            }
        }

        queue.MarkAsComplete();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? previousDueTime = null;
        var previousPriority = DurableJobPriority.High;
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            Assert.True(seen.Add(context.Job.Id), $"Job '{context.Job.Id}' was dequeued more than once.");

            if (previousDueTime == context.Job.DueTime)
            {
                Assert.True(
                    context.Job.Priority <= previousPriority,
                    $"Priority increased within bucket {context.Job.DueTime:O}: {previousPriority} -> {context.Job.Priority}.");
            }
            else
            {
                Assert.True(
                    previousDueTime is null || context.Job.DueTime > previousDueTime,
                    $"Due time moved backwards: {previousDueTime:O} -> {context.Job.DueTime:O}.");
            }

            previousDueTime = context.Job.DueTime;
            previousPriority = context.Job.Priority;
            Assert.True(queue.RemoveJob(context.Job.Id));
        }

        Assert.Equal(jobCount, seen.Count);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task GetAsyncEnumerator_WhenNewMinuteFramesArriveDuringBacklog_DrainsEveryJobExactlyOnce()
    {
        const int bucketCount = 6;
        const int jobsPerBucket = 5_000;
        const int jobCount = bucketCount * jobsPerBucket;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var now = timeProvider.GetUtcNow();

        EnqueueBucket(bucketIndex: 0);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? previousDueTime = null;
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            Assert.True(seen.Add(context.Job.Id), $"Job '{context.Job.Id}' was dequeued more than once.");
            Assert.True(
                previousDueTime is null || context.Job.DueTime >= previousDueTime,
                $"Due time moved backwards: {previousDueTime:O} -> {context.Job.DueTime:O}.");
            previousDueTime = context.Job.DueTime;
            Assert.True(queue.RemoveJob(context.Job.Id));

            if (seen.Count == 100)
            {
                // Simulate five new minute frames arriving while the first 5,000-job
                // frame is still being drained by a slower executor.
                for (var bucketIndex = 1; bucketIndex < bucketCount; bucketIndex++)
                {
                    EnqueueBucket(bucketIndex);
                }

                queue.MarkAsComplete();
            }

            if (seen.Count % 1_000 == 0)
            {
                await Task.Yield();
            }
        }

        Assert.Equal(jobCount, seen.Count);
        Assert.Equal(0, queue.Count);

        void EnqueueBucket(int bucketIndex)
        {
            var dueTime = now.AddMinutes(bucketIndex - bucketCount);
            for (var jobIndex = 0; jobIndex < jobsPerBucket; jobIndex++)
            {
                var priority = (DurableJobPriority)((jobIndex % 3) - 1);
                var id = $"bucket-{bucketIndex:D2}-job-{jobIndex:D4}";
                queue.Enqueue(CreateJob(id, dueTime, priority: priority), 0);
            }
        }
    }

    [Fact]
    public async Task GetAsyncEnumerator_IncrementsDequeueCount()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddMilliseconds(-100));

        queue.Enqueue(job, 0);
        queue.MarkAsComplete();

        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            Assert.Equal(1, context.DequeueCount);
            break;
        }
    }

    [Fact]
    public async Task GetAsyncEnumerator_WithInitialDequeueCount_IncrementsCorrectly()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddMilliseconds(-100));

        queue.Enqueue(job, 3);
        queue.MarkAsComplete();

        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            Assert.Equal(4, context.DequeueCount);
            break;
        }
    }

    [Fact]
    public async Task GetAsyncEnumerator_WaitsForDueTime()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var futureTime = timeProvider.GetUtcNow().AddSeconds(1);
        var job = CreateJob("job1", futureTime);

        queue.Enqueue(job, 0);
        queue.MarkAsComplete();

        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        Assert.False(moveNextTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        Assert.True(await moveNextTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(job.Id, enumerator.Current.Job.Id);
    }

    [Fact]
    public async Task GetAsyncEnumerator_WhenDueJobIsEnqueued_WakesWithoutAdvancingTime()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);

        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        await Task.Yield();

        var job = CreateJob("job1", timeProvider.GetUtcNow());
        queue.Enqueue(job, 0);

        Assert.True(await moveNextTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(job.Id, enumerator.Current.Job.Id);
    }

    [Fact]
    public async Task RetryJobLater_WhenNextDueTimeMovesEarlier_WakesWaitingEnumerator()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var job = CreateJob("job1", timeProvider.GetUtcNow().AddHours(1));
        queue.Enqueue(job, 0);

        await using var enumerator = queue.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        Assert.False(moveNextTask.IsCompleted);

        Assert.True(queue.RetryJobLater(job.Id, timeProvider.GetUtcNow(), 3));

        Assert.True(await moveNextTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(job.Id, enumerator.Current.Job.Id);
        Assert.Equal(4, enumerator.Current.DequeueCount);
    }

    [Fact]
    public async Task GetAsyncEnumerator_CompletesWhenQueueIsMarkedComplete()
    {
        var queue = new InMemoryJobQueue();
        queue.MarkAsComplete();

        var count = 0;
        await foreach (var _ in queue.WithCancellation(CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public void RemoveJob_RemovesJobFromQueue()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddSeconds(5));

        queue.Enqueue(job, 0);
        var removed = queue.RemoveJob("job1");

        Assert.True(removed);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RemoveJob_PreventsJobFromBeingDequeued()
    {
        var queue = new InMemoryJobQueue();
        var job1 = CreateJob("job1", DateTimeOffset.UtcNow.AddMilliseconds(-100));
        var job2 = CreateJob("job2", DateTimeOffset.UtcNow.AddMilliseconds(-50));

        queue.Enqueue(job1, 0);
        queue.Enqueue(job2, 0);
        queue.RemoveJob("job1");
        queue.MarkAsComplete();

        var results = new List<string>();
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            results.Add(context.Job.Id);
            if (results.Count >= 1) break;
        }

        Assert.Single(results);
        Assert.Equal("job2", results[0]);
    }

    [Fact]
    public void RemoveJob_NonExistentJob_DoesNotThrow()
    {
        var queue = new InMemoryJobQueue();

        var removed = queue.RemoveJob("non-existent-job");

        Assert.False(removed);
        Assert.Equal(0, queue.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RemoveJob_InvalidJobId_Throws(string? jobId)
    {
        var queue = new InMemoryJobQueue();

        var exception = Assert.ThrowsAny<ArgumentException>(() => queue.RemoveJob(jobId!));

        Assert.Equal("jobId", exception.ParamName);
    }

    [Fact]
    public async Task RetryJobLater_AfterCancel_DoesNotResurrectJob()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddMilliseconds(-100));
        queue.Enqueue(job, 0);
        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await enumerator.MoveNextAsync());
        var attempt = enumerator.Current;

        Assert.True(queue.RemoveJob(job.Id));
        queue.RetryJobLater(attempt, DateTimeOffset.UtcNow.AddMilliseconds(-50));
        queue.MarkAsComplete();

        Assert.Equal(0, queue.Count);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public void RetryJobLater_MovesJobToNewDueTime()
    {
        var queue = new InMemoryJobQueue();
        var originalDueTime = DateTimeOffset.UtcNow.AddSeconds(1);
        var job = CreateJob("job1", originalDueTime);

        queue.Enqueue(job, 0);

        var context = CreateJobContext(job, "run1", 1);
        var newDueTime = DateTimeOffset.UtcNow.AddSeconds(10);

        queue.RetryJobLater(context, newDueTime);

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task RetryJobLater_PreservesDequeueCount()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddMilliseconds(-100));

        queue.Enqueue(job, 5);

        var context = CreateJobContext(job, "run1", 5);
        var newDueTime = DateTimeOffset.UtcNow.AddMilliseconds(-50);

        queue.RetryJobLater(context, newDueTime);
        queue.MarkAsComplete();

        await foreach (var newContext in queue.WithCancellation(CancellationToken.None))
        {
            Assert.Equal(6, newContext.DequeueCount);
            Assert.Equal("job1", newContext.Job.Id);
            break;
        }
    }

    [Fact]
    public void RetryJobLater_PreservesTraceContext()
    {
        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        const string traceState = "vendor=value";
        var queue = new InMemoryJobQueue();
        var job = CreateJob(
            "job1",
            DateTimeOffset.UtcNow.AddSeconds(1),
            traceParent,
            traceState,
            priority: DurableJobPriority.High);

        queue.Enqueue(job, 0);
        queue.RetryJobLater(CreateJobContext(job, "run1", 1), DateTimeOffset.UtcNow.AddSeconds(10));

        var retriedJob = Assert.Single(queue.GetSnapshot()).Job;
        Assert.Equal(traceParent, retriedJob.TraceParent);
        Assert.Equal(traceState, retriedJob.TraceState);
        Assert.Equal(DurableJobPriority.High, retriedJob.Priority);
    }

    [Fact]
    public void RetryJobLater_NonExistentJob_DoesNotThrow()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddSeconds(1));
        var context = CreateJobContext(job, "run1", 1);

        queue.RetryJobLater(context, DateTimeOffset.UtcNow.AddSeconds(10));

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task GetAsyncEnumerator_RespectsEmptyBuckets()
    {
        var queue = new InMemoryJobQueue();
        var dueTime = DateTimeOffset.UtcNow.AddMilliseconds(-100);
        var job1 = CreateJob("job1", dueTime);
        var job2 = CreateJob("job2", dueTime);

        queue.Enqueue(job1, 0);
        queue.Enqueue(job2, 0);
        queue.RemoveJob("job1");
        queue.RemoveJob("job2");
        queue.MarkAsComplete();

        var results = new List<IJobRunContext>();
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            results.Add(context);
            if (results.Count >= 2) break;
        }

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetAsyncEnumerator_HandlesMultipleDueTimes()
    {
        var queue = new InMemoryJobQueue();
        var now = DateTimeOffset.UtcNow;
        var job1 = CreateJob("job1", now.AddSeconds(-5));
        var job2 = CreateJob("job2", now.AddSeconds(-3));
        var job3 = CreateJob("job3", now.AddSeconds(-1));

        queue.Enqueue(job1, 0);
        queue.Enqueue(job2, 0);
        queue.Enqueue(job3, 0);
        queue.MarkAsComplete();

        var results = new List<string>();
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            results.Add(context.Job.Name);
            if (results.Count >= 3) break;
        }

        Assert.Equal(3, results.Count);
        Assert.Equal("job1", results[0]);
        Assert.Equal("job2", results[1]);
        Assert.Equal("job3", results[2]);
    }

    [Fact]
    public async Task GetAsyncEnumerator_GeneratesUniqueRunIds()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddMilliseconds(-100));

        queue.Enqueue(job, 0);
        queue.MarkAsComplete();

        var runIds = new List<string>();
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            runIds.Add(context.RunId);
            Assert.False(string.IsNullOrEmpty(context.RunId));
            break;
        }

        Assert.Single(runIds);
    }

    [Fact]
    public async Task GetAsyncEnumerator_CancellationToken_StopsEnumeration()
    {
        var queue = new InMemoryJobQueue();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in queue.WithCancellation(cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Enqueue_WithSameDueTimeAsDrainingBucket_DoesNotStrandJob()
    {
        // Regression: Enqueueing a job with the same DueTime as a bucket currently being
        // drained by the enumerator previously reused the dequeued bucket via _buckets,
        // but that bucket was no longer in the priority queue, so the new job was stranded.
        var queue = new InMemoryJobQueue();
        var sharedDueTime = DateTimeOffset.UtcNow.AddMilliseconds(-100);
        var job1 = CreateJob("job1", sharedDueTime);
        var job2 = CreateJob("job2", sharedDueTime);

        queue.Enqueue(job1, 0);

        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);

        // Drain the first job. After this returns, the enumerator is between yields with
        // the bucket already dequeued from _queue.
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("job1", enumerator.Current.Job.Name);
        queue.RemoveJob(enumerator.Current.Job.Id);

        // Enqueue a second job at the same DueTime. With the bug, this would target the
        // already-dequeued bucket and never become visible to the enumerator.
        queue.Enqueue(job2, 0);
        queue.MarkAsComplete();

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("job2", enumerator.Current.Job.Name);
        queue.RemoveJob(enumerator.Current.Job.Id);

        Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Enqueue_AfterBucketProcessingCompletes_CreatesNewBucket()
    {
        // After a bucket is fully drained and removed, a subsequent Enqueue at the same
        // DueTime must create a fresh bucket and re-enter the priority queue so the new
        // job is processed.
        var queue = new InMemoryJobQueue();
        var sharedDueTime = DateTimeOffset.UtcNow.AddMilliseconds(-100);
        var job1 = CreateJob("job1", sharedDueTime);

        queue.Enqueue(job1, 0);

        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("job1", enumerator.Current.Job.Name);
        queue.RemoveJob(enumerator.Current.Job.Id);

        // job1 has been yielded and removed; a new job at the same DueTime must be visible
        // to the next MoveNextAsync.
        var job2 = CreateJob("job2", sharedDueTime);
        queue.Enqueue(job2, 0);
        queue.MarkAsComplete();

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("job2", enumerator.Current.Job.Name);
        queue.RemoveJob(enumerator.Current.Job.Id);

        Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Enqueue_SameJobIdMovedDuringBucketDrain_YieldsOnlyReplacement()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var staleDueTime = timeProvider.GetUtcNow().AddMinutes(-2);
        var replacementDueTime = timeProvider.GetUtcNow().AddMinutes(-1);
        var sentinel = CreateJob("sentinel", staleDueTime);
        var stale = CreateJob("replaced-job", staleDueTime);
        var replacement = CreateJob("replaced-job", replacementDueTime);

        queue.Enqueue(sentinel, 0);
        queue.Enqueue(stale, 0);

        await using var enumerator = queue.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(sentinel.Id, enumerator.Current.Job.Id);
        Assert.True(queue.RemoveJob(sentinel.Id));

        queue.Enqueue(replacement, 7);
        queue.MarkAsComplete();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Same(replacement, enumerator.Current.Job);
        Assert.Equal(8, enumerator.Current.DequeueCount);
        Assert.True(queue.RemoveJob(replacement.Id));

        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RemoveLastDequeuedJob_AfterCompletion_WakesWaitingEnumerator()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var job = CreateJob("in-flight", timeProvider.GetUtcNow().AddMilliseconds(-100));
        queue.Enqueue(job, 0);

        await using var enumerator = queue.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, queue.Count);

        queue.MarkAsComplete();
        var completion = enumerator.MoveNextAsync().AsTask();
        Assert.False(completion.IsCompleted);

        Assert.True(queue.RemoveJob(job.Id));
        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task CancelLastDetachedJob_AfterCompletion_WakesWaitingEnumerator()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var job = CreateJob("in-flight", timeProvider.GetUtcNow().AddMilliseconds(-100));
        queue.Enqueue(job, 0);

        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await enumerator.MoveNextAsync());

        queue.MarkAsComplete();
        var completion = enumerator.MoveNextAsync().AsTask();
        Assert.False(completion.IsCompleted);

        Assert.True(queue.RemoveJob(job.Id));
        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Enqueue_SameJobIdMovedDuringBucketDrain_YieldsOnlyLatestVersion()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var staleDueTime = timeProvider.GetUtcNow().AddMinutes(-2);
        var replacementDueTime = timeProvider.GetUtcNow().AddMinutes(-1);
        var stale = CreateJob("replaced-job", staleDueTime, priority: DurableJobPriority.Low);
        var sentinel = CreateJob("sentinel", staleDueTime, priority: DurableJobPriority.High);
        var replacement = CreateJob("replaced-job", replacementDueTime, priority: DurableJobPriority.High);

        queue.Enqueue(stale, 0);
        queue.Enqueue(sentinel, 0);

        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);

        // Taking the first item snapshots the stale version in the dequeued bucket.
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(sentinel.Id, enumerator.Current.Job.Id);
        Assert.True(queue.RemoveJob(sentinel.Id));

        // Moving the same ID to a new bucket must invalidate the stale snapshot item.
        queue.Enqueue(replacement, 7);
        queue.MarkAsComplete();

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(replacement.Id, enumerator.Current.Job.Id);
        Assert.Equal(replacementDueTime, enumerator.Current.Job.DueTime);
        Assert.Equal(DurableJobPriority.High, enumerator.Current.Job.Priority);
        Assert.Equal(8, enumerator.Current.DequeueCount);
        Assert.True(queue.RemoveJob(replacement.Id));

        Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsyncEnumerator_FullBatchMutations_ValidateEachEntryAtMostOnce()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var dueTime = timeProvider.GetUtcNow().AddMinutes(-1);
        for (var index = 0; index < InMemoryJobQueue.MaxDequeueBatchSize; index++)
        {
            queue.Enqueue(CreateJob(index.ToString(CultureInfo.InvariantCulture), dueTime), 0);
        }

        queue.MarkAsComplete();
        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);
        var observed = 0;
        while (await enumerator.MoveNextAsync())
        {
            observed++;
            Assert.True(queue.RemoveJob(enumerator.Current.Job.Id));
        }

        Assert.Equal(InMemoryJobQueue.MaxDequeueBatchSize, observed);
        Assert.Equal(InMemoryJobQueue.MaxDequeueBatchSize, queue.ValidationProbeCount);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task OneMillionSameTimeJobs_DequeueInBoundedPriorityOrder()
    {
        const int JobCount = 1_000_000;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryJobQueue(timeProvider);
        var dueTime = timeProvider.GetUtcNow().AddMinutes(-1);

        for (var index = 0; index < JobCount; index++)
        {
            var priority = (index % 3) switch
            {
                0 => DurableJobPriority.Low,
                1 => DurableJobPriority.Normal,
                _ => DurableJobPriority.High,
            };
            queue.Enqueue(CreateJob(index.ToString(), dueTime, priority: priority), 0);
        }

        await using var enumerator = queue.GetAsyncEnumerator(CancellationToken.None);
        for (var index = 0; index < InMemoryJobQueue.MaxDequeueBatchSize; index++)
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(DurableJobPriority.High, enumerator.Current.Job.Priority);
        }

        Assert.Equal(JobCount, queue.Count);
    }

    private static DurableJob CreateJob(
        string id,
        DateTimeOffset dueTime,
        string? traceParent = null,
        string? traceState = null,
        DurableJobPriority priority = DurableJobPriority.Normal)
    {
        return new DurableJob
        {
            Id = id,
            Name = id,
            DueTime = dueTime,
            TargetGrainId = GrainId.Create("test", id),
            ShardId = "shard1",
            Metadata = null,
            TraceParent = traceParent,
            TraceState = traceState,
            Priority = priority,
        };
    }

    private static IJobRunContext CreateJobContext(DurableJob job, string runId, int dequeueCount)
    {
        var context = Substitute.For<IJobRunContext>();
        context.Job.Returns(job);
        context.RunId.Returns(runId);
        context.DequeueCount.Returns(dequeueCount);
        return context;
    }
}
