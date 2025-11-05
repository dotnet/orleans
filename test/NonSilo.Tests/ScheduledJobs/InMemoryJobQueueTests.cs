using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableJobs;
using Orleans.Runtime;
using NSubstitute;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("DurableJobs")]
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

        var results = new List<IDurableJobContext>();
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
        var queue = new InMemoryJobQueue();
        var futureTime = DateTimeOffset.UtcNow.AddSeconds(2);
        var job = CreateJob("job1", futureTime);

        queue.Enqueue(job, 0);
        queue.MarkAsComplete();

        var startTime = DateTimeOffset.UtcNow;
        await foreach (var context in queue.WithCancellation(CancellationToken.None))
        {
            var elapsed = DateTimeOffset.UtcNow - startTime;
            Assert.True(elapsed.TotalSeconds >= 1.5, $"Job was dequeued too early. Elapsed: {elapsed.TotalSeconds}s");
            break;
        }
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
    public void CancelJob_RemovesJobFromQueue()
    {
        var queue = new InMemoryJobQueue();
        var job = CreateJob("job1", DateTimeOffset.UtcNow.AddSeconds(5));

        queue.Enqueue(job, 0);
        var removed = queue.CancelJob("job1");

        Assert.True(removed);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task CancelJob_PreventsJobFromBeingDequeued()
    {
        var queue = new InMemoryJobQueue();
        var job1 = CreateJob("job1", DateTimeOffset.UtcNow.AddMilliseconds(-100));
        var job2 = CreateJob("job2", DateTimeOffset.UtcNow.AddMilliseconds(-50));

        queue.Enqueue(job1, 0);
        queue.Enqueue(job2, 0);
        queue.CancelJob("job1");
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
    public void CancelJob_NonExistentJob_DoesNotThrow()
    {
        var queue = new InMemoryJobQueue();

        var removed = queue.CancelJob("non-existent-job");

        Assert.False(removed);
        Assert.Equal(0, queue.Count);
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
        queue.CancelJob("job1");
        queue.CancelJob("job2");
        queue.MarkAsComplete();

        var results = new List<IDurableJobContext>();
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
        var cts = new CancellationTokenSource();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in queue.WithCancellation(cts.Token))
            {
            }
        });
    }

    private static DurableJob CreateJob(string id, DateTimeOffset dueTime)
    {
        return new DurableJob
        {
            Id = id,
            Name = id,
            DueTime = dueTime,
            TargetGrainId = GrainId.Create("test", id),
            ShardId = "shard1",
            Metadata = null
        };
    }

    private static IDurableJobContext CreateJobContext(DurableJob job, string runId, int dequeueCount)
    {
        var context = Substitute.For<IDurableJobContext>();
        context.Job.Returns(job);
        context.RunId.Returns(runId);
        context.DequeueCount.Returns(dequeueCount);
        return context;
    }
}
