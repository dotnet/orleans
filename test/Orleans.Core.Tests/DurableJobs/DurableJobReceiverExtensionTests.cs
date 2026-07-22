using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Concurrency;
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
    public async Task HandleDurableJobAsync_WhenHandlerCancelsWithoutCanceledToken_ReturnsFailure()
    {
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled(new CancellationToken(canceled: true)));

        var extension = CreateExtension(handler);
        var context = CreateJobContext("run-1");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.True(result.IsFailed);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenAttemptTokenIsCanceled_PropagatesAndAllowsSameAttemptToRestart()
    {
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var token = callInfo.Arg<CancellationToken>();
                return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
            });
        var extension = CreateExtension(handler);
        var context = CreateJobContext("run-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extension.HandleDurableJobAsync(context, cts.Token).AsTask());
        var restarted = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, restarted.Status);
        await handler.Received(2).ExecuteJobAsync(context, Arg.Any<CancellationToken>());
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
    public async Task HandleDurableJobAsync_WhenExecutionIsPending_UsesConfiguredTimeProviderForLongPoll()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
        var executionTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>()).Returns(executionTask.Task);
        var pollInterval = TimeSpan.FromSeconds(10);
        var extension = CreateExtension(handler, pollInterval, timeProvider);

        var resultTask = extension.HandleDurableJobAsync(CreateJobContext("run-1"), CancellationToken.None).AsTask();
        await Task.Yield();
        Assert.False(resultTask.IsCompleted);

        timeProvider.Advance(pollInterval);
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));

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
    public async Task HandleDurableJobAsync_WhenSameJobIdentityIsUsedByDifferentShards_ExecutesBoth()
    {
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var extension = CreateExtension(handler);
        var first = CreateJobContext("run-1", jobId: "shared-id", shardId: "shard-1");
        var second = CreateJobContext("run-2", jobId: "shared-id", shardId: "shard-2");

        Assert.Equal(DurableJobRunStatus.Completed, (await extension.HandleDurableJobAsync(first, CancellationToken.None)).Status);
        Assert.Equal(DurableJobRunStatus.Completed, (await extension.HandleDurableJobAsync(second, CancellationToken.None)).Status);

        await handler.Received(2).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenExecutionDispatchThrowsSynchronously_DoesNotCacheAnInvalidAttempt()
    {
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(handler);
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var shared = new DurableJobReceiverExtensionShared(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions()),
            Options.Create(new SiloMessagingOptions()),
            grainFactory);
        var execution = new DurableJobExecutionExtension(grainContext, shared);
        var dispatchCount = 0;
        grainFactory.GetGrain<IDurableJobExecutionExtension>(Arg.Any<GrainId>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref dispatchCount) == 1)
            {
                throw new InvalidOperationException("injected dispatch failure");
            }

            return execution;
        });
        var extension = new DurableJobReceiverExtension(grainContext, shared);
        var context = CreateJobContext("run-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => extension.HandleDurableJobAsync(context, CancellationToken.None).AsTask());
        var retried = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, retried.Status);
        await handler.Received(1).ExecuteJobAsync(context, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ExecutionExtension_DoesNotAllowHandlerExecutionToInterleaveWithNormalGrainCalls()
    {
        var pollingMethod = typeof(IDurableJobReceiverExtension).GetMethod(nameof(IDurableJobReceiverExtension.HandleDurableJobAsync));
        var executionMethod = typeof(IDurableJobExecutionExtension).GetMethod(nameof(IDurableJobExecutionExtension.ExecuteDurableJobAsync));

        Assert.NotNull(pollingMethod?.GetCustomAttributes(typeof(AlwaysInterleaveAttribute), inherit: true).SingleOrDefault());
        Assert.Empty(executionMethod!.GetCustomAttributes(typeof(AlwaysInterleaveAttribute), inherit: true));
    }

    [Fact]
    public void DurableJobRunResult_Failed_ThrowsForNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DurableJobRunResult.Failed(null!));
    }

    [Fact]
    public void DurableJobRunResult_PollAfter_RejectsDelayBeyondRuntimeTimerLimit()
    {
        var delay = DurableJobTimeLimits.MaximumTimerDelay.Add(TimeSpan.FromMilliseconds(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => DurableJobRunResult.PollAfter(delay));
    }

    private static DurableJobReceiverExtension CreateExtension(
        IDurableJobHandler handler,
        TimeSpan? jobStatusPollInterval = null,
        TimeProvider timeProvider = null)
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(handler);
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var shared = new DurableJobReceiverExtensionShared(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions { JobStatusPollInterval = jobStatusPollInterval ?? TimeSpan.FromSeconds(1) }),
            Options.Create(new SiloMessagingOptions()),
            grainFactory,
            timeProvider ?? TimeProvider.System);
        var execution = new DurableJobExecutionExtension(grainContext, shared);
        grainFactory.GetGrain<IDurableJobExecutionExtension>(Arg.Any<GrainId>()).Returns(execution);
        return new DurableJobReceiverExtension(grainContext, shared);
    }

    private static IJobRunContext CreateJobContext(
        string runId,
        string jobId = "job-1",
        int dequeueCount = 1,
        string shardId = "shard-1")
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
            ShardId = shardId
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
