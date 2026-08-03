using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
public class JobShardTests
{
    [Fact]
    public async Task TryScheduleJobAsync_ThrowsArgumentOutOfRangeException_WhenDueTimeBeforeStartTime()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(1);
        var shard = new TestJobShard("shard-1", start, end);

        var request = new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "target"),
            JobName = "job",
            DueTime = start.AddSeconds(-1),
        };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => shard.TryScheduleJobAsync(request, CancellationToken.None));
        Assert.Equal("request", ex.ParamName);
        Assert.Equal(0, await shard.GetJobCountAsync());
    }

    [Fact]
    public async Task TryScheduleJobAsync_ThrowsArgumentOutOfRangeException_WhenDueTimeAfterEndTime()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var end = DateTimeOffset.UtcNow;
        var shard = new TestJobShard("shard-1", start, end);

        var request = new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "target"),
            JobName = "job",
            DueTime = end.AddSeconds(1),
        };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => shard.TryScheduleJobAsync(request, CancellationToken.None));
        Assert.Equal("request", ex.ParamName);
        Assert.Equal(0, await shard.GetJobCountAsync());
    }

    [Fact]
    public async Task TryScheduleJobAsync_AllowsDueTimeExactlyAtShardBoundaries()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var end = DateTimeOffset.UtcNow.AddMinutes(30);
        var shard = new TestJobShard("shard-1", start, end);

        var atStart = await shard.TryScheduleJobAsync(new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "target-start"),
            JobName = "job-at-start",
            DueTime = start,
        }, CancellationToken.None);

        var atEnd = await shard.TryScheduleJobAsync(new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "target-end"),
            JobName = "job-at-end",
            DueTime = end,
        }, CancellationToken.None);

        Assert.NotNull(atStart);
        Assert.NotNull(atEnd);
        Assert.Equal(2, await shard.GetJobCountAsync());
    }

    [Fact]
    public async Task RescheduleJobAsync_ResetsDequeueCountToZero_WhereasRetryJobLaterAsync_PreservesIt()
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(-1);
        var end = now.AddHours(1);

        // --- RescheduleJobAsync path: resets dequeue count to 0. ---
        var rescheduleShard = new TestJobShard("shard-reschedule", start, end);
        var rescheduleJob = await rescheduleShard.TryScheduleJobAsync(new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "reschedule-target"),
            JobName = "reschedule-job",
            DueTime = now.AddSeconds(-2),
        }, CancellationToken.None);
        Assert.NotNull(rescheduleJob);

        var firstRescheduleAttempt = await ConsumeNextAsync(rescheduleShard);
        Assert.Equal(1, firstRescheduleAttempt.DequeueCount);

        var rescheduleDueTime = now.AddSeconds(-1);
        await rescheduleShard.RescheduleJobAsync(firstRescheduleAttempt, rescheduleDueTime, CancellationToken.None);

        var afterReschedule = await ConsumeNextAsync(rescheduleShard);
        Assert.Equal(rescheduleJob!.Id, afterReschedule.Job.Id);
        Assert.Equal(1, afterReschedule.DequeueCount);
        Assert.Equal(1, rescheduleShard.PersistRetryCallCount);
        Assert.Equal(rescheduleJob.Id, rescheduleShard.LastPersistedRetryJobId);
        Assert.Equal(rescheduleDueTime, rescheduleShard.LastPersistedRetryDueTime);

        // --- RetryJobLaterAsync path: preserves/increments the existing dequeue count. ---
        var retryShard = new TestJobShard("shard-retry", start, end);
        var retryJob = await retryShard.TryScheduleJobAsync(new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "retry-target"),
            JobName = "retry-job",
            DueTime = now.AddSeconds(-2),
        }, CancellationToken.None);
        Assert.NotNull(retryJob);

        var firstRetryAttempt = await ConsumeNextAsync(retryShard);
        Assert.Equal(1, firstRetryAttempt.DequeueCount);

        var retryDueTime = now.AddSeconds(-1);
        await retryShard.RetryJobLaterAsync(firstRetryAttempt, retryDueTime, CancellationToken.None);

        var afterRetry = await ConsumeNextAsync(retryShard);
        Assert.Equal(retryJob!.Id, afterRetry.Job.Id);
        Assert.Equal(2, afterRetry.DequeueCount);
        Assert.Equal(1, retryShard.PersistRetryCallCount);
        Assert.Equal(retryJob.Id, retryShard.LastPersistedRetryJobId);
        Assert.Equal(retryDueTime, retryShard.LastPersistedRetryDueTime);

        // The two reschedule paths must diverge on dequeue-count semantics.
        Assert.NotEqual(afterReschedule.DequeueCount, afterRetry.DequeueCount);
    }

    private static async Task<IJobRunContext> ConsumeNextAsync(IJobShard shard)
    {
        await using var enumerator = shard.ConsumeDurableJobsAsync().GetAsyncEnumerator(CancellationToken.None);
        var moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(moved, "Expected a due job to be available in the shard queue.");
        return enumerator.Current;
    }

    private sealed class TestJobShard : JobShard
    {
        public int PersistRetryCallCount { get; private set; }

        public string? LastPersistedRetryJobId { get; private set; }

        public DateTimeOffset? LastPersistedRetryDueTime { get; private set; }

        public TestJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime)
            : base(id, startTime, endTime)
        {
        }

        protected override Task PersistAddJobAsync(string jobId, string jobName, DateTimeOffset dueTime, GrainId target, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        protected override Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        protected override Task PersistRetryJobAsync(string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken)
        {
            PersistRetryCallCount++;
            LastPersistedRetryJobId = jobId;
            LastPersistedRetryDueTime = newDueTime;
            return Task.CompletedTask;
        }
    }
}
