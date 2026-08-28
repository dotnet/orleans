using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Diagnostics;
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
    public void HandleDurableJobAsync_NullContext_Throws()
    {
        var extension = CreateExtension(Substitute.For<IDurableJobHandler>());

        var exception = Assert.Throws<ArgumentNullException>(
            () => extension.HandleDurableJobAsync(null!, CancellationToken.None));

        Assert.Equal("context", exception.ParamName);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenHandlerCancelsWithoutAttemptCancellation_ReturnsFailure()
    {
        var exception = new OperationCanceledException("Handler-specific cancellation");
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(exception));

        var extension = CreateExtension(handler);
        var context = CreateJobContext("run-1");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.True(result.IsFailed);
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public void HandleDurableJobAsync_WhenHandlerResolutionFails_DoesNotPoisonAttemptCache()
    {
        var extension = CreateExtension(new object());
        var context = CreateJobContext("run-1");

        Assert.Throws<InvalidOperationException>(() => extension.HandleDurableJobAsync(context, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => extension.HandleDurableJobAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenPollTokenIsCanceled_StopsPollButExecutionRemainsRunning()
    {
        var executionTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(executionTask.Task);

        var timeProvider = new TimerTrackingFakeTimeProvider(DateTimeOffset.UtcNow);
        var extension = CreateExtension(
            handler,
            jobStatusPollInterval: TimeSpan.FromHours(1),
            timeProvider: timeProvider);
        var context = CreateJobContext("run-1");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var firstCall = extension.HandleDurableJobAsync(context, cts.Token).AsTask();
        await timeProvider.TimerCreated;
        Assert.False(firstCall.IsCompleted);
        Assert.Equal(1, timeProvider.ActiveTimerCount);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => firstCall);
        var second = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.True(second.IsInProgress);
        Assert.Equal(0, timeProvider.ActiveTimerCount);
        Assert.False(executionTask.Task.IsCompleted);
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
    public async Task HandleDurableJobAsync_WhenExecutionCompletesDuringLongPoll_CancelsTimeout()
    {
        var executionTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(executionTask.Task);
        var timeProvider = new TimerTrackingFakeTimeProvider(DateTimeOffset.UtcNow);
        var extension = CreateExtension(handler, TimeSpan.FromMinutes(1), timeProvider);
        var context = CreateJobContext("run-1");

        var resultTask = extension.HandleDurableJobAsync(context, CancellationToken.None).AsTask();
        await timeProvider.TimerCreated;
        Assert.Equal(1, timeProvider.ActiveTimerCount);

        executionTask.SetResult(true);

        Assert.Equal(DurableJobRunStatus.Completed, (await resultTask).Status);
        Assert.Equal(0, timeProvider.ActiveTimerCount);
    }

    [Fact]
    public async Task HandleDurableJobAsync_CoalescesActiveDeliveriesCachesCompletionAndStartsNewGeneration()
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

        var completed = await first.AsTask().WaitAsync(
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(DurableJobRunStatus.Completed, completed.Status);
        await handler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());

        var redeliveryAfterCompletion = await extension.HandleDurableJobAsync(secondNotification, CancellationToken.None);
        Assert.Equal(DurableJobRunStatus.Completed, redeliveryAfterCompletion.Status);
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
    public async Task HandleDurableJobAsync_FeatureNameMatchTakesPrecedenceOverGrainHandler()
    {
        var grainHandler = Substitute.For<IDurableJobHandler>();
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(DurableJobRunResult.Completed));
        var registry = new DurableJobHandlerRegistry();
        RegisterFeatureHandler(registry, featureHandler);
        var extension = CreateExtension(grainHandler, registry: registry);
        var context = CreateJobContext("run-1", jobName: "feature");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        await featureHandler.Received(1).ExecuteJobAsync(context, Arg.Any<CancellationToken>());
        await grainHandler.DidNotReceive().ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDurableJobAsync_UnregisteredFeatureNameFallsBackToGrainHandler()
    {
        var grainHandler = Substitute.For<IDurableJobHandler>();
        grainHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        var registry = new DurableJobHandlerRegistry();
        RegisterFeatureHandler(registry, featureHandler, static jobName => jobName == "other-feature");
        var extension = CreateExtension(grainHandler, registry: registry);
        var context = CreateJobContext("run-1", jobName: "grain-job");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        await grainHandler.Received(1).ExecuteJobAsync(context, Arg.Any<CancellationToken>());
        await featureHandler.DidNotReceive().ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void HandleDurableJobAsync_WhenMultipleFeatureHandlersMatch_RejectsAmbiguousDispatch()
    {
        var grainHandler = Substitute.For<IDurableJobHandler>();
        var firstFeatureHandler = Substitute.For<IDurableJobFeatureHandler>();
        var secondFeatureHandler = Substitute.For<IDurableJobFeatureHandler>();
        var registry = new DurableJobHandlerRegistry();
        RegisterFeatureHandler(registry, firstFeatureHandler);
        RegisterFeatureHandler(registry, secondFeatureHandler);
        var extension = CreateExtension(grainHandler, registry: registry);
        var context = CreateJobContext("run-1", jobName: "feature");

        var exception = Assert.Throws<InvalidOperationException>(
            () => extension.HandleDurableJobAsync(context, CancellationToken.None));

        Assert.Contains("Multiple durable job feature handlers match job 'feature'", exception.Message, StringComparison.Ordinal);
        grainHandler.DidNotReceive().ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
        firstFeatureHandler.DidNotReceive().ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
        secondFeatureHandler.DidNotReceive().ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerReturnsFailed_RecordsFailureTelemetryOnce()
    {
        using var telemetry = new HandlerTelemetryCapture();
        var exception = new InvalidOperationException("Explicit feature failure");
        var expected = DurableJobRunResult.Failed(exception);
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(expected));
        var registry = new DurableJobHandlerRegistry();
        RegisterFeatureHandler(registry, featureHandler);
        var extension = CreateExtension(
            Substitute.For<IDurableJobHandler>(),
            registry: registry,
            durableJobsInstruments: telemetry.Instruments);
        var firstContext = CreateJobContext("run-explicit-failure-1", jobName: "feature");
        var secondContext = CreateJobContext("run-explicit-failure-2", jobName: "feature");

        var firstResult = await extension.HandleDurableJobAsync(firstContext, CancellationToken.None);
        var secondResult = await extension.HandleDurableJobAsync(secondContext, CancellationToken.None);

        Assert.Same(expected, firstResult);
        Assert.Same(expected, secondResult);
        await featureHandler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
        telemetry.AssertOutcome(firstContext.RunId, "failed", ActivityStatusCode.Error, exception);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerThrows_RecordsFailureTelemetryOnce()
    {
        using var telemetry = new HandlerTelemetryCapture();
        var exception = new InvalidOperationException("Thrown feature failure");
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<DurableJobRunResult>(exception));
        var registry = new DurableJobHandlerRegistry();
        RegisterFeatureHandler(registry, featureHandler);
        var extension = CreateExtension(
            Substitute.For<IDurableJobHandler>(),
            registry: registry,
            durableJobsInstruments: telemetry.Instruments);
        var firstContext = CreateJobContext("run-thrown-failure-1", jobName: "feature");
        var secondContext = CreateJobContext("run-thrown-failure-2", jobName: "feature");

        var firstResult = await extension.HandleDurableJobAsync(firstContext, CancellationToken.None);
        var secondResult = await extension.HandleDurableJobAsync(secondContext, CancellationToken.None);

        Assert.True(firstResult.IsFailed);
        Assert.True(secondResult.IsFailed);
        Assert.Same(exception, firstResult.Exception);
        Assert.Same(exception, secondResult.Exception);
        await featureHandler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
        telemetry.AssertOutcome(firstContext.RunId, "failed", ActivityStatusCode.Error, exception);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerReturnsNull_RecordsFailureTelemetryOnce()
    {
        using var telemetry = new HandlerTelemetryCapture();
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<DurableJobRunResult>(null!));
        var registry = new DurableJobHandlerRegistry();
        RegisterFeatureHandler(registry, featureHandler);
        var extension = CreateExtension(
            Substitute.For<IDurableJobHandler>(),
            registry: registry,
            durableJobsInstruments: telemetry.Instruments);
        var firstContext = CreateJobContext("run-null-result-1", jobName: "feature");
        var secondContext = CreateJobContext("run-null-result-2", jobName: "feature");

        var firstResult = await extension.HandleDurableJobAsync(firstContext, CancellationToken.None);
        var secondResult = await extension.HandleDurableJobAsync(secondContext, CancellationToken.None);

        Assert.True(firstResult.IsFailed);
        Assert.True(secondResult.IsFailed);
        var firstException = Assert.IsType<InvalidOperationException>(firstResult.Exception);
        var secondException = Assert.IsType<InvalidOperationException>(secondResult.Exception);
        Assert.Equal(firstException.Message, secondException.Message);
        await featureHandler.Received(1).ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>());
        telemetry.AssertOutcome(firstContext.RunId, "failed", ActivityStatusCode.Error, firstException);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerCancelsWithoutAttemptCancellation_RecordsFailureTelemetryOnce()
    {
        using var telemetry = new HandlerTelemetryCapture();
        var exception = new OperationCanceledException("Handler-specific cancellation");
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<DurableJobRunResult>(exception));
        var registry = new DurableJobHandlerRegistry();
        RegisterFeatureHandler(registry, featureHandler);
        var extension = CreateExtension(
            Substitute.For<IDurableJobHandler>(),
            registry: registry,
            durableJobsInstruments: telemetry.Instruments);
        var context = CreateJobContext("run-handler-canceled", jobName: "feature");

        var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.True(result.IsFailed);
        Assert.Same(exception, result.Exception);
        telemetry.AssertOutcome(context.RunId, "failed", ActivityStatusCode.Error, exception);
    }

    [Fact]
    public async Task HandleDurableJobAsync_WhenFeatureHandlerReturnsSuccessfulDisposition_RecordsCompletedTelemetryOnce()
    {
        using var telemetry = new HandlerTelemetryCapture();
        var outcomes = new[]
        {
            (RunIds: new[] { "run-completed-1", "run-completed-2" }, Result: DurableJobRunResult.Completed),
            (RunIds: new[] { "run-in-progress" }, Result: DurableJobRunResult.InProgress(TimeSpan.FromSeconds(1))),
            (RunIds: new[] { "run-rescheduled-1", "run-rescheduled-2" }, Result: DurableJobRunResult.RescheduleAt(DateTimeOffset.UtcNow.AddHours(1)))
        };
        var expectedInvocationCount = outcomes.Length;

        foreach (var outcome in outcomes)
        {
            var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
            featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromResult(outcome.Result));
            var registry = new DurableJobHandlerRegistry();
            RegisterFeatureHandler(registry, featureHandler);
            var extension = CreateExtension(
                Substitute.For<IDurableJobHandler>(),
                registry: registry,
                durableJobsInstruments: telemetry.Instruments);

            foreach (var runId in outcome.RunIds)
            {
                var context = CreateJobContext(runId, jobName: "feature");
                var result = await extension.HandleDurableJobAsync(context, CancellationToken.None);

                Assert.Same(outcome.Result, result);
            }

            await featureHandler.Received(1).ExecuteJobAsync(
                Arg.Any<IJobRunContext>(),
                Arg.Any<CancellationToken>());
        }

        foreach (var outcome in outcomes)
        {
            telemetry.AssertOutcome(
                outcome.RunIds[0],
                "completed",
                ActivityStatusCode.Ok,
                expectedInvocationCount: expectedInvocationCount);
        }
    }

    [Fact]
    public async Task HandleDurableJobAsync_FeatureInProgressReinvokesAfterDelayWithoutConcurrentDuplicates()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var secondInvocationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondInvocation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var featureHandler = Substitute.For<IDurableJobFeatureHandler>();
        featureHandler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => ExecuteAsync());
        var registry = new DurableJobHandlerRegistry();
        RegisterFeatureHandler(registry, featureHandler);
        var extension = CreateExtension(
            Substitute.For<IDurableJobHandler>(),
            jobStatusPollInterval: TimeSpan.FromSeconds(1),
            timeProvider: timeProvider,
            registry: registry);
        var context = CreateJobContext("run-1", jobName: "feature");

        var first = await extension.HandleDurableJobAsync(context, CancellationToken.None);
        var duplicateBeforeDue = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.InProgress, first.Status);
        Assert.Equal(DurableJobRunStatus.InProgress, duplicateBeforeDue.Status);
        Assert.Equal(1, Volatile.Read(ref invocationCount));

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var continuation = extension.HandleDurableJobAsync(context, CancellationToken.None);
        await secondInvocationStarted.Task;
        var concurrentDuplicate = await extension.HandleDurableJobAsync(context, CancellationToken.None);

        Assert.True(concurrentDuplicate.IsInProgress);
        Assert.Equal(2, Volatile.Read(ref invocationCount));

        releaseSecondInvocation.SetResult();
        Assert.True((await continuation).IsInProgress);
        var attemptTask = new DurableJobReceiverExtension.TestAccessor(extension).GetAttemptTask(context);
        Assert.NotNull(attemptTask);
        await attemptTask;
        Assert.Equal(
            DurableJobRunStatus.Completed,
            (await extension.HandleDurableJobAsync(context, CancellationToken.None)).Status);
        await featureHandler.Received(2).ExecuteJobAsync(context, Arg.Any<CancellationToken>());

        async ValueTask<DurableJobRunResult> ExecuteAsync()
        {
            if (Interlocked.Increment(ref invocationCount) == 1)
            {
                return DurableJobRunResult.InProgress(TimeSpan.FromSeconds(5));
            }

            secondInvocationStarted.SetResult();
            await releaseSecondInvocation.Task;
            return DurableJobRunResult.Completed;
        }
    }

    [Fact]
    public async Task TurnIsolationFilter_ReceiverPollProgressesWhileHandlerLeasePreventsDuplicateExecution()
    {
        var isolation = new DurableJobTurnIsolation();
        isolation.Enable();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(execution.Task);
        var timeProvider = new FakeTimeProvider();
        var registry = new DurableJobHandlerRegistry(isolation);
        var extension = CreateExtension(
            handler,
            jobStatusPollInterval: TimeSpan.FromSeconds(1),
            timeProvider: timeProvider,
            registry: registry);
        var jobContext = CreateJobContext("poll-under-isolation");

        var initialDelivery = extension.HandleDurableJobAsync(jobContext, CancellationToken.None).AsTask();
        await Task.Yield();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await initialDelivery).IsInProgress);

        var callContext = Substitute.For<IIncomingGrainCallContext>();
        callContext.TargetId.Returns(GrainId.Create("test", "grain-1"));
        callContext.InterfaceMethod.Returns(
            typeof(IDurableJobReceiverExtension).GetMethod(
                nameof(IDurableJobReceiverExtension.HandleDurableJobAsync))!);
        DurableJobRunResult? pollResult = null;
        callContext.Invoke().Returns(async _ =>
        {
            pollResult = await extension.HandleDurableJobAsync(jobContext, CancellationToken.None);
        });

        await new DurableJobTurnIsolationFilter().Invoke(callContext)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotNull(pollResult);
        Assert.True(pollResult.IsInProgress);
        await handler.Received(1).ExecuteJobAsync(jobContext, Arg.Any<CancellationToken>());

        RequestContext.Remove(DurableJobTurnIsolation.RequestContextKey);
        var ordinaryTurn = isolation.EnterOrdinaryAsync();
        Assert.False(ordinaryTurn.IsCompleted);
        execution.SetResult();
        using var lease = await ordinaryTurn.AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await handler.Received(1).ExecuteJobAsync(jobContext, Arg.Any<CancellationToken>());
    }

    private static void RegisterFeatureHandler(
        DurableJobHandlerRegistry registry,
        IDurableJobFeatureHandler handler,
        Func<string, bool>? canHandle = null)
    {
        canHandle ??= static jobName => jobName == "feature";
        handler.CanHandle(Arg.Any<string>()).Returns(call => canHandle(call.Arg<string>()));
        registry.Register(handler);
    }

    private static DurableJobReceiverExtension CreateExtension(
        object handler,
        TimeSpan? jobStatusPollInterval = null,
        TimeProvider? timeProvider = null,
        DurableJobHandlerRegistry? registry = null,
        DurableJobsInstruments? durableJobsInstruments = null)
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(handler);
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));
        var shared = new DurableJobReceiverExtensionShared(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions
            {
                JobStatusPollInterval = jobStatusPollInterval ?? TimeSpan.FromSeconds(1)
            }),
            Options.Create(new SiloMessagingOptions()),
            timeProvider ?? TimeProvider.System,
            durableJobsInstruments);
        return new DurableJobReceiverExtension(grainContext, shared, registry);
    }

    private static IJobRunContext CreateJobContext(
        string runId,
        string jobId = "job-1",
        int dequeueCount = 1,
        long executionGeneration = 0,
        string? jobName = null)
    {
        var context = Substitute.For<IJobRunContext>();
        context.RunId.Returns(runId);
        context.DequeueCount.Returns(dequeueCount);
        context.Job.Returns(new DurableJob
        {
            Id = jobId,
            Name = jobName ?? jobId,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = GrainId.Create("test", "grain-1"),
            ShardId = "shard-1",
            ExecutionGeneration = executionGeneration
        });

        return context;
    }

    private sealed class TimerTrackingFakeTimeProvider(DateTimeOffset startDateTime) : FakeTimeProvider(startDateTime)
    {
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeTimerCount;

        public int ActiveTimerCount => Volatile.Read(ref _activeTimerCount);

        public Task TimerCreated => _timerCreated.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _activeTimerCount);
            _timerCreated.TrySetResult();
            return new TrackingTimer(this, timer);
        }

        private sealed class TrackingTimer(TimerTrackingFakeTimeProvider owner, ITimer timer) : ITimer
        {
            private readonly TimerTrackingFakeTimeProvider _owner = owner;
            private readonly ITimer _timer = timer;
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => _timer.Change(dueTime, period);

            public void Dispose()
            {
                try
                {
                    _timer.Dispose();
                }
                finally
                {
                    CompleteDisposal();
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await _timer.DisposeAsync();
                }
                finally
                {
                    CompleteDisposal();
                }
            }

            private void CompleteDisposal()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref _owner._activeTimerCount);
                }
            }
        }
    }

    private sealed class HandlerTelemetryCapture : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _activities = new();
        private readonly ServiceProvider _serviceProvider;
        private readonly ActivityListener _activityListener;
        private readonly MetricCollector<long> _startedCollector;
        private readonly MetricCollector<long> _executionCollector;
        private readonly string? _trackedRunId;
        private readonly TaskCompletionSource _trackedHandlerStopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HandlerTelemetryCapture(string? trackedRunId = null)
        {
            _trackedRunId = trackedRunId;
            var services = new ServiceCollection();
            services.AddMetrics();
            _serviceProvider = services.BuildServiceProvider();
            var meterFactory = _serviceProvider.GetRequiredService<IMeterFactory>();
            Instruments = new DurableJobsInstruments(new OrleansInstruments(meterFactory));
            _startedCollector = new MetricCollector<long>(
                meterFactory,
                "Microsoft.Orleans",
                "orleans-durablejobs-handler-executions-started");
            _executionCollector = new MetricCollector<long>(
                meterFactory,
                "Microsoft.Orleans",
                "orleans-durablejobs-handler-executions");
            var activitySource = DurableJobsDiagnostics.Source;
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, activitySource),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    _activities.Enqueue(activity);
                    if (_trackedRunId is not null
                        && activity.GetTagItem(ActivityTagKeys.DurableJobRunId) is string runId
                        && runId == _trackedRunId)
                    {
                        _trackedHandlerStopped.TrySetResult();
                    }
                }
            };
            ActivitySource.AddActivityListener(_activityListener);
        }

        public DurableJobsInstruments Instruments { get; }

        public Task TrackedHandlerStopped => _trackedHandlerStopped.Task;

        public void AssertOutcome(
            string runId,
            string expectedMetricStatus,
            ActivityStatusCode expectedActivityStatus,
            Exception? exception = null,
            int expectedInvocationCount = 1)
        {
            var started = _startedCollector.GetMeasurementSnapshot();
            Assert.Equal(expectedInvocationCount, started.Count);
            Assert.All(started, static measurement => Assert.Equal(1, measurement.Value));
            var executions = _executionCollector.GetMeasurementSnapshot();
            Assert.Equal(expectedInvocationCount, executions.Count);
            Assert.All(executions, measurement =>
            {
                Assert.Equal(1, measurement.Value);
                Assert.Equal(expectedMetricStatus, measurement.Tags["status"]);
            });

            var activity = Assert.Single(
                _activities,
                activity => activity.OperationName == DurableJobsDiagnostics.ActivityExecuteJobHandler
                    && activity.GetTagItem(ActivityTagKeys.DurableJobRunId) is string activityRunId
                    && activityRunId == runId);
            Assert.Equal(expectedActivityStatus, activity.Status);
            if (exception is null)
            {
                Assert.Null(activity.GetTagItem(ActivityTagKeys.ExceptionType));
                Assert.Null(activity.GetTagItem(ActivityTagKeys.ExceptionMessage));
                Assert.Null(activity.StatusDescription);
            }
            else
            {
                Assert.Equal(exception.GetType().FullName, activity.GetTagItem(ActivityTagKeys.ExceptionType));
                Assert.Equal(exception.Message, activity.GetTagItem(ActivityTagKeys.ExceptionMessage));
                Assert.Equal(exception.Message, activity.StatusDescription);
            }
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _executionCollector.Dispose();
            _startedCollector.Dispose();
            _serviceProvider.Dispose();
        }
    }
}
