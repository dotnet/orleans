using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.ScheduledJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class DurableJobReceiverExtensionTests
{
    [Fact]
    public async Task HandleDurableJobAsync_WhenExecutionTaskIsCanceled_PropagatesCancellation()
    {
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled(new CancellationToken(canceled: true)));

        var extension = CreateExtension(handler);
        var context = CreateJobContext("run-1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extension.HandleDurableJobAsync(context, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenTokenIsCanceledButExecutionIsStillRunning_RemainsRunning()
    {
        var executionTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(executionTask.Task);

        var extension = CreateExtension(handler);
        var context = CreateJobContext("run-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var first = await extension.HandleDurableJobAsync(context, cts.Token);
        var second = await extension.HandleDurableJobAsync(context, cts.Token);

        Assert.True(first.IsInProgress);
        Assert.True(second.IsInProgress);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        executionTask.SetResult(true);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenExecutionIsInProgress_UsesConfiguredPollInterval()
    {
        var executionTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(executionTask.Task);
        var pollInterval = TimeSpan.FromMilliseconds(25);

        var extension = CreateExtension(handler, pollInterval);
        var context = CreateJobContext("run-1");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.True(result.IsInProgress);
        Assert.Equal(pollInterval, result.PollAfterDelay);

        executionTask.SetResult(true);
    }

    [Fact]
    public async Task HandleDurableJobAsync_DeduplicatesRunAndAllowsNewRunWithResetDequeueCount()
    {
        var executionTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(executionTask.Task);

        var extension = CreateExtension(handler, TimeSpan.FromMinutes(1));
        var firstNotification = CreateJobContext("run-1", jobId: "job-1", dequeueCount: 1);
        var secondNotification = CreateJobContext("run-1", jobId: "job-1", dequeueCount: 1);

        var first = extension.HandleDurableJobAsync(firstNotification, CancellationToken.None);
        var second = await extension.HandleDurableJobAsync(secondNotification, CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.True(second.IsInProgress);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        executionTask.SetResult(true);

        var completed = await first.AsTask().WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(DurableJobRunStatus.Completed, completed.Status);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        var duplicateAfterCompletion = await extension.HandleDurableJobAsync(secondNotification, CancellationToken.None);
        Assert.Equal(DurableJobRunStatus.Completed, duplicateAfterCompletion.Status);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        var nextAttempt = CreateJobContext("run-2", jobId: "job-1", dequeueCount: 1);
        var retryResult = await extension.HandleDurableJobAsync(nextAttempt, CancellationToken.None);
        Assert.Equal(DurableJobRunStatus.Completed, retryResult.Status);
        await handler.Received(2).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenExecutionGenerationChanges_StartsNewExecution()
    {
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var extension = CreateExtension(handler);
        var firstRun = CreateJobContext("run-1", executionGeneration: 0);
        var rescheduledRun = CreateJobContext("run-2", executionGeneration: 1);

        Assert.Equal(DurableJobRunStatus.Completed, (await extension.HandleDurableJobAsync(firstRun, CancellationToken.None)).Status);
        Assert.Equal(DurableJobRunStatus.Completed, (await extension.HandleDurableJobAsync(rescheduledRun, CancellationToken.None)).Status);
        await handler.Received(2).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    private static DurableJobReceiverExtension CreateExtension(IDurableJobHandler handler, TimeSpan? jobStatusPollInterval = null)
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(handler);
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));
        var shared = new DurableJobReceiverExtensionShared(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions { JobStatusPollInterval = jobStatusPollInterval ?? TimeSpan.FromSeconds(1) }),
            Options.Create(new SiloMessagingOptions()),
            TimeProvider.System);
        return new DurableJobReceiverExtension(grainContext, shared);
    }

    private static IJobRunContext CreateJobContext(
        string runId,
        string jobId = "job-1",
        int dequeueCount = 1,
        long executionGeneration = 0)
    {
        var context = Substitute.For<IJobRunContext>();
        context.RunId.Returns(runId);
        context.DequeueCount.Returns(dequeueCount);
        context.Job.Returns(new DurableJob
        {
            Id = jobId,
            Name = jobId,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = GrainId.Create("test", "grain-1"),
            ShardId = "shard-1",
            ExecutionGeneration = executionGeneration
        });

        return context;
    }
}
