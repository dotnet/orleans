using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.ScheduledJobs;

[TestCategory("DurableJobs")]
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
    public async Task HandleDurableJobAsync_WhenTokenIsCanceledButExecutionIsStillRunning_RemainsPending()
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

        Assert.True(first.IsPending);
        Assert.True(second.IsPending);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        executionTask.SetResult(true);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenExecutionIsPending_UsesConfiguredPollInterval()
    {
        var executionTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(executionTask.Task);
        var pollInterval = TimeSpan.FromMilliseconds(25);

        var extension = CreateExtension(handler, pollInterval);
        var context = CreateJobContext("run-1");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.True(result.IsPending);
        Assert.Equal(pollInterval, result.PollAfterDelay);

        executionTask.SetResult(true);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenSameJobAttemptHasDifferentRunIds_DeduplicatesExecution()
    {
        var executionTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(executionTask.Task);

        var extension = CreateExtension(handler);
        var firstNotification = CreateJobContext("run-1", jobId: "job-1", dequeueCount: 1);
        var secondNotification = CreateJobContext("run-2", jobId: "job-1", dequeueCount: 1);

        var first = await extension.HandleDurableJobAsync(firstNotification, CancellationToken.None);
        var second = await extension.HandleDurableJobAsync(secondNotification, CancellationToken.None);

        Assert.True(first.IsPending);
        Assert.True(second.IsPending);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        executionTask.SetResult(true);

        var completed = await WaitForTerminalResult(extension, firstNotification);
        Assert.Equal(DurableJobRunStatus.Completed, completed.Status);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        var duplicateAfterCompletion = await extension.HandleDurableJobAsync(secondNotification, CancellationToken.None);
        Assert.Equal(DurableJobRunStatus.Completed, duplicateAfterCompletion.Status);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        var nextAttempt = CreateJobContext("run-3", jobId: "job-1", dequeueCount: 2);
        var retryResult = await extension.HandleDurableJobAsync(nextAttempt, CancellationToken.None);
        Assert.Equal(DurableJobRunStatus.Completed, retryResult.Status);
        await handler.Received(2).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DurableJobRunResult_Failed_ThrowsForNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DurableJobRunResult.Failed(null!));
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

    private static IJobRunContext CreateJobContext(string runId, string jobId = "job-1", int dequeueCount = 1)
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
            ShardId = "shard-1"
        });

        return context;
    }

    private static async Task<DurableJobRunResult> WaitForTerminalResult(DurableJobReceiverExtension extension, IJobRunContext context)
    {
        for (var i = 0; i < 10; i++)
        {
            var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);
            if (!result.IsPending)
            {
                return result;
            }

            await Task.Yield();
        }

        throw new TimeoutException("Durable job receiver did not observe terminal job status.");
    }
}
