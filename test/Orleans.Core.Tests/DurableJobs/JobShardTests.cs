using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestArea("DurableJobs")]
[TestCategory("DurableJobs")]
public class JobShardTests
{
    [Fact]
    public async Task TryScheduleJobAsync_ForwardsCompleteJobForPersistence()
    {
        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        const string traceState = "vendor=value";
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var target = GrainId.Create("test", "job");
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var shard = new TestJobShard(dueTime.AddMinutes(-1), dueTime.AddMinutes(1));

        var job = await shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = target,
                JobName = "job",
                DueTime = dueTime,
                Metadata = metadata,
                TraceParent = traceParent,
                TraceState = traceState,
            },
            CancellationToken.None);

        var persistedJob = Assert.IsType<DurableJob>(shard.PersistedJob);
        Assert.Same(job, persistedJob);
        Assert.Equal(target, persistedJob.TargetGrainId);
        Assert.Equal("job", persistedJob.Name);
        Assert.Equal(dueTime, persistedJob.DueTime);
        Assert.Same(metadata, persistedJob.Metadata);
        Assert.Equal(traceParent, persistedJob.TraceParent);
        Assert.Equal(traceState, persistedJob.TraceState);
    }

    [Fact]
    public async Task RetryJobLaterAsync_ForwardsCompleteRunContextForPersistence()
    {
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var shard = new TestJobShard(dueTime.AddMinutes(-1), dueTime.AddMinutes(1));
        var job = await shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = GrainId.Create("test", "job"),
                JobName = "job",
                DueTime = dueTime,
            },
            CancellationToken.None);
        var context = Substitute.For<IJobRunContext>();
        context.Job.Returns(job);
        context.RunId.Returns("run");
        context.DequeueCount.Returns(3);
        var retryTime = dueTime.AddMinutes(1);

        await shard.RetryJobLaterAsync(context, retryTime, CancellationToken.None);

        var persistedContext = Assert.IsAssignableFrom<IJobRunContext>(shard.PersistedRetryContext);
        Assert.Same(context, persistedContext);
        Assert.Equal(retryTime, shard.PersistedRetryTime);
        Assert.Equal(3, persistedContext.DequeueCount);
    }

    [Fact]
    public async Task SuccessfulRescheduleResetsDequeueCountAndCreatesNewRunId()
    {
        var now = DateTimeOffset.UtcNow;
        var resetShard = await CreateShardWithDueJobAsync("reset", now);
        var resetRun = await ConsumeNextAsync(resetShard);

        await resetShard.RescheduleJobAsync(resetRun, now.AddSeconds(-1), CancellationToken.None);

        var rescheduledRun = await ConsumeNextAsync(resetShard);
        Assert.Equal(1, rescheduledRun.DequeueCount);
        Assert.NotEqual(resetRun.RunId, rescheduledRun.RunId);
        Assert.Equal(resetRun.Job.ExecutionGeneration + 1, rescheduledRun.Job.ExecutionGeneration);
        Assert.Equal(0, resetShard.PersistedRetryContext!.DequeueCount);

        var retryShard = await CreateShardWithDueJobAsync("retry", now);
        var firstFailure = await ConsumeNextAsync(retryShard);

        await retryShard.RetryJobLaterAsync(firstFailure, now.AddSeconds(-1), CancellationToken.None);

        var retriedRun = await ConsumeNextAsync(retryShard);
        Assert.Equal(2, retriedRun.DequeueCount);
        Assert.Equal(1, retryShard.PersistedRetryContext!.DequeueCount);
    }

    private static async Task<TestJobShard> CreateShardWithDueJobAsync(string id, DateTimeOffset now)
    {
        var shard = new TestJobShard(now.AddHours(-1), now.AddHours(1));
        Assert.NotNull(await shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = GrainId.Create("test", id),
                JobName = id,
                DueTime = now.AddSeconds(-2)
            },
            CancellationToken.None));
        return shard;
    }

    private static async Task<IJobRunContext> ConsumeNextAsync(IJobShard shard)
    {
        await using var enumerator = shard.ConsumeDurableJobsAsync().GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private sealed class TestJobShard(DateTimeOffset startTime, DateTimeOffset endTime)
        : JobShard("shard", startTime, endTime)
    {
        public DurableJob? PersistedJob { get; private set; }

        public IJobRunContext? PersistedRetryContext { get; private set; }

        public DateTimeOffset PersistedRetryTime { get; private set; }

        protected override Task PersistAddJobAsync(DurableJob job, CancellationToken cancellationToken)
        {
            PersistedJob = job;
            return Task.CompletedTask;
        }

        protected override Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task PersistRetryJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken)
        {
            PersistedRetryContext = jobContext;
            PersistedRetryTime = newDueTime;
            return Task.CompletedTask;
        }
    }
}
