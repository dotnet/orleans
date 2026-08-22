using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
    public async Task HandleDurableJobAsync_DeduplicatesExecutionAndAllowsNewGeneration()
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

        var nextAttempt = CreateJobContext("run-2", jobId: "job-1", dequeueCount: 1, executionGeneration: 1);
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

    [Fact]
    public async Task PruneCompletedJobAttempts_WhenRetentionNotElapsed_KeepsCompletedStateTrackedAndAvoidsReExecution()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var extension = CreateExtension(handler, timeProvider: timeProvider);
        var context = CreateJobContext("run-1");

        var completed = await extension.HandleDurableJobAsync(context, CancellationToken.None);
        Assert.Equal(DurableJobRunStatus.Completed, completed.Status);

        // Advance by less than the 1-minute retention window: PruneCompletedJobAttempts (invoked at entry of
        // the next call) must hit its "not yet expired and not over the count limit" branch and return
        // immediately, leaving the completed attempt (and its cached JobAttemptState) tracked.
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var stillCached = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, stillCached.Status);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneCompletedJobAttempts_WhenRetentionElapsed_RemovesTrackedStateAndAllowsReExecution()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var extension = CreateExtension(handler, timeProvider: timeProvider);
        var context = CreateJobContext("run-1");

        var completed = await extension.HandleDurableJobAsync(context, CancellationToken.None);
        Assert.Equal(DurableJobRunStatus.Completed, completed.Status);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        // Advance past the 1-minute retention window. PruneCompletedJobAttempts (invoked at the entry of the
        // next call, before the new key lookup) must now take the "expired" branch, dequeue the completed
        // attempt, and remove its corresponding entry from the job-attempts dictionary since the tracked
        // state's CompletedTimestamp still matches the queued record.
        timeProvider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        // Re-submitting the identical (JobId, RunId) key must now be treated as a brand-new execution
        // (exists == false) rather than returning the stale cached result, because the prior state was pruned.
        var reExecuted = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, reExecuted.Status);
        await handler.Received(2).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneCompletedJobAttempts_WhenElapsedTimeExactlyEqualsRetention_TreatsAttemptAsExpired()
    {
        // Pins the exact ">= CompletedJobAttemptRetention" boundary: an off-by-one mutation to ">" would
        // leave the attempt tracked at precisely the 1-minute mark, whereas the 30-second/61-second tests
        // above only exercise deep-interior points on either side of the boundary and would not catch it.
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var extension = CreateExtension(handler, timeProvider: timeProvider);
        var context = CreateJobContext("run-1");

        var completed = await extension.HandleDurableJobAsync(context, CancellationToken.None);
        Assert.Equal(DurableJobRunStatus.Completed, completed.Status);

        timeProvider.Advance(TimeSpan.FromMinutes(1));

        var reExecuted = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, reExecuted.Status);
        await handler.Received(2).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerRegisteredForJobName_UsesFeatureHandlerInsteadOfGrainHandler()
    {
        var registry = new DurableJobHandlerRegistry();
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobRunResult.Completed);
        registry.Register("featured-job", featureHandler);

        // The grain instance intentionally does NOT implement IDurableJobHandler: the feature-handler
        // branch must be used instead of requiring the grain to implement the interface.
        var extension = CreateExtensionWithRegistry(new object(), registry);
        var context = CreateJobContext("run-1", jobId: "featured-job");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        await featureHandler.Received(1).ExecuteJobAsync(context, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenNoFeatureHandlerMatchesJobName_FallsBackToGrainHandler()
    {
        var registry = new DurableJobHandlerRegistry();
        var unrelatedFeatureHandler = Substitute.For<IDurableJobFeatureHandler>();
        registry.Register("other-job", unrelatedFeatureHandler);

        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var extension = CreateExtensionWithRegistry(handler, registry);
        var context = CreateJobContext("run-1", jobId: "unrelated-job");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        await handler.Received(1).ExecuteJobAsync(context, Arg.Any<CancellationToken>());
        await unrelatedFeatureHandler.DidNotReceive().ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerThrows_ReturnsFailedResultWithException()
    {
        var registry = new DurableJobHandlerRegistry();
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        var thrown = new InvalidOperationException("feature handler exploded");
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<DurableJobRunResult>(thrown));
        registry.Register("featured-job", featureHandler);

        var extension = CreateExtensionWithRegistry(new object(), registry);
        var context = CreateJobContext("run-1", jobId: "featured-job");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.True(result.IsFailed);
        Assert.Equal(DurableJobRunStatus.Failed, result.Status);
        Assert.Same(thrown, result.Exception);
        await featureHandler.Received(1).ExecuteJobAsync(context, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerReturnsNull_ReturnsFailedResultWithInvalidOperationException()
    {
        var registry = new DurableJobHandlerRegistry();
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<DurableJobRunResult>((DurableJobRunResult)null!));
        registry.Register("featured-job", featureHandler);

        var extension = CreateExtensionWithRegistry(new object(), registry);
        var context = CreateJobContext("run-1", jobId: "featured-job");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        var exception = Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.True(result.IsFailed);
        Assert.Contains("featured-job", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerIsCanceled_PropagatesCancellation()
    {
        var registry = new DurableJobHandlerRegistry();
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromCanceled<DurableJobRunResult>(new CancellationToken(canceled: true)));
        registry.Register("featured-job", featureHandler);

        var extension = CreateExtensionWithRegistry(new object(), registry);
        var context = CreateJobContext("run-1", jobId: "featured-job");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extension.HandleDurableJobAsync(context, CancellationToken.None).AsTask());
        await featureHandler.Received(1).ExecuteJobAsync(context, Arg.Any<CancellationToken>());
    }

    private static DurableJobReceiverExtension CreateExtensionWithRegistry(object grainInstance, DurableJobHandlerRegistry registry, TimeSpan? jobStatusPollInterval = null)
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(grainInstance);
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));
        var shared = new DurableJobReceiverExtensionShared(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions { JobStatusPollInterval = jobStatusPollInterval ?? TimeSpan.FromSeconds(1) }),
            Options.Create(new SiloMessagingOptions()),
            TimeProvider.System);
        return new DurableJobReceiverExtension(grainContext, shared, registry);
    }

    private static DurableJobReceiverExtension CreateExtension(IDurableJobHandler handler, TimeSpan? jobStatusPollInterval = null, TimeProvider? timeProvider = null)
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(handler);
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));
        var shared = new DurableJobReceiverExtensionShared(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions { JobStatusPollInterval = jobStatusPollInterval ?? TimeSpan.FromSeconds(1) }),
            Options.Create(new SiloMessagingOptions()),
            timeProvider ?? TimeProvider.System);
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
