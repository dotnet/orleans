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
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task TryScheduleJobAsync_InvalidJobName_Throws(string? jobName)
    {
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var shard = new TestJobShard(dueTime.AddMinutes(-1), dueTime.AddMinutes(1));
        var request = new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "job"),
            JobName = jobName!,
            DueTime = dueTime
        };

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => shard.TryScheduleJobAsync(request, CancellationToken.None));

        Assert.Equal("JobName", exception.ParamName);
    }

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
    public async Task TryScheduleJobAsync_StableJobIdIsIdempotent()
    {
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var shard = new TestJobShard(dueTime.AddMinutes(-1), dueTime.AddMinutes(1));
        var request = new ScheduleJobRequest
        {
            JobId = "stable-job",
            Target = GrainId.Create("test", "job"),
            JobName = "job",
            DueTime = dueTime
        };

        var first = await shard.TryScheduleJobAsync(request, CancellationToken.None);
        var second = await shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                JobId = request.JobId,
                Target = request.Target,
                JobName = request.JobName,
                DueTime = dueTime.AddSeconds(1),
                Metadata = request.Metadata,
                TraceParent = "different-attempt-trace"
            },
            CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal("stable-job", first!.Id);
        Assert.Equal(1, shard.PersistAddCount);
        Assert.Equal(1, await shard.GetJobCountAsync());
    }

    [Fact]
    public async Task TryScheduleJobAsync_ConflictingStableJobIdIsRejected()
    {
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var shard = new TestJobShard(dueTime.AddMinutes(-1), dueTime.AddMinutes(1));
        await shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                JobId = "stable-job",
                Target = GrainId.Create("test", "job"),
                JobName = "job",
                DueTime = dueTime
            },
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => shard.TryScheduleJobAsync(
                new ScheduleJobRequest
                {
                    JobId = "stable-job",
                    Target = GrainId.Create("test", "other"),
                    JobName = "job",
                    DueTime = dueTime
                },
                CancellationToken.None));

        Assert.Contains("different properties", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task RetryJobLaterAsync_AfterCancellationRequest_DoesNotResurrectJob()
    {
        var now = DateTimeOffset.UtcNow;
        var shard = await CreateShardWithDueJobAsync("canceled", now);
        var attempt = await ConsumeNextAsync(shard);

        Assert.Equal(
            DurableJobMutationResult.Applied,
            await shard.RemoveJobAsync(attempt.Job.Id, CancellationToken.None));
        await shard.RetryJobLaterAsync(attempt, now.AddSeconds(-1), CancellationToken.None);
        await shard.MarkAsCompleteAsync(CancellationToken.None);

        Assert.Equal(0, await shard.GetJobCountAsync());
        Assert.Null(shard.PersistedRetryContext);
        await using var enumerator = shard.ConsumeDurableJobsAsync().GetAsyncEnumerator(CancellationToken.None);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task AttemptCancellation_WithoutRemoval_PreservesJob()
    {
        var now = DateTimeOffset.UtcNow;
        var shard = await CreateShardWithDueJobAsync("attempt-canceled", now);

        var firstAttempt = await ConsumeNextAsync(shard);

        Assert.Equal("attempt-canceled", firstAttempt.Job.Name);
        Assert.Equal(1, await shard.GetJobCountAsync());
    }

    [Fact]
    public async Task MarkAsCompleteAsync_WaitsForInFlightSchedulePersistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var shard = new GateableJobShard(now.AddMinutes(-1), now.AddMinutes(1));
        var scheduleTask = shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = GrainId.Create("test", "job"),
                JobName = "job",
                DueTime = now
            },
            cancellationToken);

        await shard.PersistAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var completionTask = shard.MarkAsCompleteAsync(cancellationToken);

        Assert.False(completionTask.IsCompleted);
        shard.AllowPersistAdd.SetResult();

        Assert.NotNull(await scheduleTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        await completionTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(shard.IsAddingCompleted);
        Assert.Equal(1, await shard.GetJobCountAsync());
        Assert.Null(await shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = GrainId.Create("test", "later"),
                JobName = "later",
                DueTime = now
            },
            cancellationToken));
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
        public int PersistAddCount { get; private set; }

        public IJobRunContext? PersistedRetryContext { get; private set; }

        public DateTimeOffset PersistedRetryTime { get; private set; }

        protected override Task PersistAddJobAsync(DurableJob job, CancellationToken cancellationToken)
        {
            PersistAddCount++;
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

    private sealed class GateableJobShard(DateTimeOffset startTime, DateTimeOffset endTime)
        : JobShard("gateable-shard", startTime, endTime)
    {
        public TaskCompletionSource PersistAddStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowPersistAdd { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task PersistAddJobAsync(DurableJob job, CancellationToken cancellationToken)
        {
            PersistAddStarted.TrySetResult();
            await AllowPersistAdd.Task.WaitAsync(cancellationToken);
        }

        protected override Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        protected override Task PersistRetryJobAsync(
            IJobRunContext jobContext,
            DateTimeOffset newDueTime,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
