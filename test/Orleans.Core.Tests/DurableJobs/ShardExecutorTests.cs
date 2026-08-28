using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Xunit;

namespace NonSilo.Tests.ScheduledJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class ShardExecutorTests
{
    [Fact]
    public async Task RunShardAsync_WhenNotOverloaded_ProcessesJobsWithoutDelay()
    {
        var options = CreateOptions(maxConcurrentJobs: 10);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(3);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify all jobs were processed and removed from the shard
        Assert.Equal(3, completedJobs.Count);
        Assert.Contains("job-0", completedJobs);
        Assert.Contains("job-1", completedJobs);
        Assert.Contains("job-2", completedJobs);

        await shard.Received(3).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WhenOverloaded_PausesAndRetriesWithBackoffDelay()
    {
        var options = CreateOptions(maxConcurrentJobs: 10, overloadBackoffDelay: TimeSpan.FromMilliseconds(50));
        var overloadDetector = Substitute.For<IOverloadDetector>();
        var jobs = CreateJobs(2);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        // Simulate system being overloaded initially, then clearing after 3 checks
        var checkCount = 0;
        overloadDetector.IsOverloaded.Returns(_ =>
        {
            checkCount++;
            return checkCount <= 3;
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Jobs should complete successfully after overload clears, and detector should be checked multiple times
        Assert.Equal(2, completedJobs.Count);
        Assert.True(checkCount > 3, $"Expected multiple overload checks, got {checkCount}");
    }

    [Fact]
    public async Task RunShardAsync_WhenOverloadTransitionsDuringProcessing_HandlesStateChanges()
    {
        var options = CreateOptions(maxConcurrentJobs: 10, overloadBackoffDelay: TimeSpan.FromMilliseconds(10));
        var overloadDetector = Substitute.For<IOverloadDetector>();
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        // Jobs arrive gradually to allow overload state to toggle during processing
        var shard = CreateJobShardWithDelayedYield(5, TimeSpan.FromMilliseconds(10));

        // Alternate overload state with each check: overloaded, not overloaded, overloaded...
        var checkCount = 0;
        overloadDetector.IsOverloaded.Returns(_ =>
        {
            checkCount++;
            return checkCount % 2 == 1;
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // All jobs should complete despite the toggling overload state
        Assert.Equal(5, completedJobs.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Contains($"job-{i}", completedJobs);
        }
    }

    [Fact]
    public async Task RunShardAsync_RespectsMaxConcurrentJobsPerSilo_WhileCheckingOverload()
    {
        var maxConcurrent = 3;
        var options = CreateOptions(maxConcurrentJobs: maxConcurrent);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var currentConcurrent = 0;
        var maxObservedConcurrent = 0;
        var concurrentLock = new object();

        var jobs = CreateJobs(10);
        var shard = CreateJobShard(jobs);

        // Track the maximum concurrent job execution count
        ConfigureGrainFactoryWithSlowJobExecution(grainFactory, async () =>
        {
            lock (concurrentLock)
            {
                currentConcurrent++;
                if (currentConcurrent > maxObservedConcurrent)
                {
                    maxObservedConcurrent = currentConcurrent;
                }
            }

            await Task.Delay(50);

            lock (concurrentLock)
            {
                currentConcurrent--;
            }
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify concurrency limit was respected while jobs executed in parallel
        Assert.True(maxObservedConcurrent <= maxConcurrent,
            $"Max concurrent jobs was {maxObservedConcurrent}, but limit was {maxConcurrent}");
        Assert.True(maxObservedConcurrent > 1,
            "Expected some concurrent execution");
    }

    [Fact]
    public async Task RunShardAsync_WhenCancelledDuringOverloadBackoff_CancelsCleanly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions(maxConcurrentJobs: 10, overloadBackoffDelay: TimeSpan.FromSeconds(10));
        var overloadDetector = CreateOverloadDetector(isOverloaded: true);
        var jobs = CreateJobs(5);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        // Cancel shortly after starting, while executor is waiting for overload to clear
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await executor.RunShardAsync(shard, cts.Token);
        }).WaitAsync(cancellationToken);

        // No jobs should have executed since cancellation occurred during backoff wait
        Assert.Empty(completedJobs);
    }

    [Fact]
    public async Task RunShardAsync_WhenJobFailsDuringOverload_ContinuesOverloadChecking()
    {
        var options = CreateOptions(
            maxConcurrentJobs: 10,
            overloadBackoffDelay: TimeSpan.FromMilliseconds(10),
            shouldRetry: (context, ex) => DateTimeOffset.UtcNow.AddSeconds(1)
        );
        var overloadDetector = Substitute.For<IOverloadDetector>();
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var jobs = CreateJobs(3);
        var shard = CreateJobShard(jobs);

        var completedJobs = new List<string>();
        var failedJobs = new List<string>();
        var jobExecutionCount = 0;

        // Periodically report overload to test interaction with job failures
        var checkCount = 0;
        overloadDetector.IsOverloaded.Returns(_ =>
        {
            checkCount++;
            return checkCount % 3 == 1;
        });

        // First job fails, remaining jobs succeed
        ConfigureGrainFactoryWithSelectiveFailures(grainFactory, completedJobs, failedJobs, ref jobExecutionCount);

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Failed job should be scheduled for retry, successful jobs should be removed
        Assert.Equal(2, completedJobs.Count);
        Assert.Single(failedJobs);

        await shard.Received(1).RetryJobLaterAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());

        await shard.Received(2).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WaitsForShardStartTime_BeforeProcessing()
    {
        var options = CreateOptions(maxConcurrentJobs: 10);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(1);
        var futureStartTime = DateTimeOffset.UtcNow.AddMilliseconds(200);
        var shard = CreateJobShard(jobs, startTime: futureStartTime);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        var startTime = DateTimeOffset.UtcNow;

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify executor waited for shard start time before processing
        var elapsed = DateTimeOffset.UtcNow - startTime;
        Assert.True(elapsed.TotalMilliseconds >= 150,
            $"Expected to wait for shard start time, but elapsed only {elapsed.TotalMilliseconds}ms");
        Assert.Single(completedJobs);
    }

    [Fact]
    public async Task RunShardAsync_WaitsForAllJobsToComplete_BeforeReturning()
    {
        var options = CreateOptions(maxConcurrentJobs: 5);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(5);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        var runningJobs = 0;
        var lockObj = new object();

        // Simulate slow job execution to ensure some run concurrently
        ConfigureGrainFactoryWithSlowJobExecution(grainFactory, async () =>
        {
            lock (lockObj) { runningJobs++; }
            await Task.Delay(100);
            lock (lockObj)
            {
                runningJobs--;
                completedJobs.Add($"job-{completedJobs.Count}");
            }
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify all jobs completed before RunShardAsync returned
        Assert.Equal(0, runningJobs);
        Assert.Equal(5, completedJobs.Count);
    }

    [Fact]
    public async Task RunShardAsync_WhenJobReturnsInProgress_EntersPollingLoopUntilCompletion()
    {
        var options = CreateOptions(maxConcurrentJobs: 10);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(1);
        var shard = CreateJobShard(jobs);

        var (grainFactory, callBox) = CreateGrainFactoryWithPollingBehavior();

        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify HandleDurableJobAsync was called 4 times (1 initial + 3 polls)
        Assert.Equal(4, callBox.Value);

        // Verify job was removed after completion
        await shard.Received(1).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WhenJobReturnsInProgress_UsesTimeProvider()
    {
        var timeProvider = new TimerTrackingFakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions(maxConcurrentJobs: 10);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(1, timeProvider.GetUtcNow().AddSeconds(-1));
        var shard = CreateJobShard(jobs, startTime: timeProvider.GetUtcNow().AddMinutes(-1));
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var currentCall = Interlocked.Increment(ref callCount);
                if (currentCall == 1)
                {
                    firstCall.SetResult();
                    return DurableJobRunResult.InProgress(TimeSpan.FromSeconds(5));
                }

                secondCall.SetResult();
                return DurableJobRunResult.Completed;
            });
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance, timeProvider);

        var runTask = executor.RunShardAsync(shard, CancellationToken.None);

        await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await timeProvider.TimerCreated.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.False(secondCall.Task.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(5));

        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await shard.Received(1).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WhenOverloaded_UsesTimeProviderForBackoff()
    {
        var timeProvider = new TimerTrackingFakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions(maxConcurrentJobs: 10, overloadBackoffDelay: TimeSpan.FromSeconds(5));
        var overloadDetector = Substitute.For<IOverloadDetector>();
        var overloaded = true;
        overloadDetector.IsOverloaded.Returns(_ => Volatile.Read(ref overloaded));
        var jobs = CreateJobs(1, timeProvider.GetUtcNow().AddSeconds(-1));
        var shard = CreateJobShard(jobs, startTime: timeProvider.GetUtcNow().AddMinutes(-1));
        var grainFactory = CreateGrainFactory();
        var jobHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureGrainFactoryWithSlowJobExecution(grainFactory, () =>
        {
            jobHandled.SetResult();
            return Task.CompletedTask;
        });
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance, timeProvider);

        var runTask = executor.RunShardAsync(shard, CancellationToken.None);

        await timeProvider.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(jobHandled.Task.IsCompleted);

        Volatile.Write(ref overloaded, false);
        timeProvider.Advance(options.Value.OverloadBackoffDelay);

        await jobHandled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunShardAsync_WhenJobReturnsInProgressThenFails_HandlesFailureCorrectly()
    {
        var options = CreateOptions(
            maxConcurrentJobs: 10,
            shouldRetry: (context, ex) => DateTimeOffset.UtcNow.AddSeconds(1)
        );
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(1);
        var shard = CreateJobShard(jobs);

        var (grainFactory, callBox) = CreateGrainFactoryWithPollingThenFailure();

        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify HandleDurableJobAsync was called 4 times (1 initial + 2 in-progress results + 1 failure)
        Assert.Equal(4, callBox.Value);

        // Verify job was scheduled for retry (not removed)
        await shard.Received(1).RetryJobLaterAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());

        await shard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WhenRetryPersistenceFails_PropagatesAfterReleasingConcurrencyAndContinuingProcessing()
    {
        var options = CreateOptions(
            maxConcurrentJobs: 1,
            shouldRetry: (context, ex) => DateTimeOffset.UtcNow.AddSeconds(1)
        );
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(2);
        var firstJobRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shard = CreateJobShard(jobs, firstJobRegistered: firstJobRegistered);
        var grainFactory = CreateGrainFactory();
        var completedJobs = new List<string>();
        var failedJobs = new List<string>();
        var jobExecutionCount = 0;
        ConfigureGrainFactoryWithSelectiveFailures(grainFactory, completedJobs, failedJobs, ref jobExecutionCount);
        var retryPersistenceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failRetryPersistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedException = new InvalidOperationException("Simulated retry persistence failure");

        shard.RetryJobLaterAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                retryPersistenceStarted.TrySetResult();
                await failRetryPersistence.Task;
                return DurableJobMutationResult.Applied;
            });

        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var runTask = executor.RunShardAsync(shard, cts.Token);

        await Task.WhenAll(firstJobRegistered.Task, retryPersistenceStarted.Task).WaitAsync(cts.Token);
        Assert.True(failRetryPersistence.TrySetException(expectedException));

        var actualException = await Assert.ThrowsAsync<InvalidOperationException>(() => runTask);

        Assert.Same(expectedException, actualException);
        Assert.Single(failedJobs);
        Assert.Single(completedJobs);
        await shard.Received(1).RetryJobLaterAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await shard.Received(1).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WhenHandlerCancelsWithoutAttemptCancellation_UsesFailureRetryPolicy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var retryPolicyInvoked = false;
        var options = CreateOptions(
            maxConcurrentJobs: 10,
            shouldRetry: (_, exception) =>
            {
                retryPolicyInvoked = true;
                Assert.IsType<TaskCanceledException>(exception);
                return DateTimeOffset.UtcNow.AddSeconds(1);
            }
        );
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(1);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactoryWithCanceledExecution();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        await executor.RunShardAsync(shard, cancellationToken);

        Assert.True(retryPolicyInvoked);
        await shard.Received(1).RetryJobLaterAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WhenAttemptCancellationIsRequested_DoesNotRetryOrRemove()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var retryPolicyInvoked = false;
        var options = CreateOptions(
            maxConcurrentJobs: 10,
            shouldRetry: (_, _) =>
            {
                retryPolicyInvoked = true;
                return DateTimeOffset.UtcNow.AddSeconds(1);
            });
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(1);
        var shard = CreateJobShard(jobs);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        var attemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(call => ExecuteAsync(call.ArgAt<CancellationToken>(1)));
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);
        using var attemptCancellation = new CancellationTokenSource();

        var runTask = executor.RunShardAsync(shard, attemptCancellation.Token);
        await attemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        attemptCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask.WaitAsync(cancellationToken));
        Assert.False(retryPolicyInvoked);
        await shard.DidNotReceive().RetryJobLaterAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        async ValueTask<DurableJobRunResult> ExecuteAsync(CancellationToken attemptCancellationToken)
        {
            attemptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, attemptCancellationToken);
            return DurableJobRunResult.Completed;
        }
    }

    [Fact]
    public async Task RunShardAsync_WhenCompletionLosesOwnership_StopsDispatchingShard()
    {
        var options = CreateOptions(maxConcurrentJobs: 1);
        var shard = CreateJobShard(CreateJobs(2));
        shard.RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobMutationResult.OwnershipLost);
        var grainFactory = CreateGrainFactory();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobRunResult.Completed);
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var services = new ServiceCollection();
        services.AddMetrics();
        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new DurableJobsInstruments(new OrleansInstruments(meterFactory));
        using var shardsProcessed = new MetricCollector<long>(
            meterFactory,
            "Microsoft.Orleans",
            "orleans-durablejobs-shards-processed");
        var executor = new ShardExecutor(
            grainFactory,
            options,
            CreateOverloadDetector(isOverloaded: false),
            NullLogger<ShardExecutor>.Instance,
            durableJobsInstruments: instruments);

        await executor.RunShardAsync(shard, CancellationToken.None);

        await extension.Received(1).HandleDurableJobAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<CancellationToken>());
        await shard.Received(1).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(
            "attempt_canceled",
            Assert.Single(shardsProcessed.GetMeasurementSnapshot()).Tags["status"]);
    }

    [Fact]
    public async Task RunShardAsync_WhenCancellationWinsBeforeAttemptReservation_DoesNotStartHandler()
    {
        var options = CreateOptions(maxConcurrentJobs: 1);
        var shard = CreateJobShard(CreateJobs(1));
        shard.TryStartAttemptAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobMutationResult.JobNotFound);
        var grainFactory = CreateGrainFactory();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var executor = new ShardExecutor(
            grainFactory,
            options,
            CreateOverloadDetector(isOverloaded: false),
            NullLogger<ShardExecutor>.Instance);

        await executor.RunShardAsync(shard, CancellationToken.None);

        await extension.DidNotReceive().HandleDurableJobAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RetryJobLaterAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_RescheduleAtReschedulesWithoutInvokingFailureRetryPolicy()
    {
        var retryPolicyInvoked = false;
        var options = CreateOptions(
            maxConcurrentJobs: 1,
            shouldRetry: (_, _) =>
            {
                retryPolicyInvoked = true;
                return null;
            });
        var shard = CreateJobShard(CreateJobs(1));
        var grainFactory = CreateGrainFactory();
        var rescheduleTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobRunResult.RescheduleAt(rescheduleTime));
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var executor = new ShardExecutor(
            grainFactory,
            options,
            CreateOverloadDetector(isOverloaded: false),
            NullLogger<ShardExecutor>.Instance);

        await executor.RunShardAsync(shard, CancellationToken.None);

        Assert.False(retryPolicyInvoked);
        await shard.Received(1).RescheduleJobAsync(
            Arg.Any<IJobRunContext>(),
            rescheduleTime,
            Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RetryJobLaterAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DurableJobMutationResult.JobNotFound)]
    [InlineData(DurableJobMutationResult.OwnershipLost)]
    public async Task RunShardAsync_WhenRetryIsNotApplied_DoesNotRecordRetry(DurableJobMutationResult mutationResult)
    {
        var options = CreateOptions(
            maxConcurrentJobs: 1,
            shouldRetry: (_, _) => DateTimeOffset.UtcNow.AddMinutes(1));
        var shard = CreateJobShard(CreateJobs(1));
        shard.RetryJobLaterAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(mutationResult);
        var grainFactory = CreateGrainFactory();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobRunResult.Failed(new InvalidOperationException("failed")));
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var services = new ServiceCollection();
        services.AddMetrics();
        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new DurableJobsInstruments(new OrleansInstruments(meterFactory));
        using var retried = new MetricCollector<long>(
            meterFactory,
            "Microsoft.Orleans",
            "orleans-durablejobs-jobs-retried");
        var executor = new ShardExecutor(
            grainFactory,
            options,
            CreateOverloadDetector(isOverloaded: false),
            NullLogger<ShardExecutor>.Instance,
            durableJobsInstruments: instruments);

        await executor.RunShardAsync(shard, CancellationToken.None);

        await shard.Received(1).RetryJobLaterAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        Assert.Empty(retried.GetMeasurementSnapshot());
    }

    [Theory]
    [InlineData(DurableJobMutationResult.JobNotFound)]
    [InlineData(DurableJobMutationResult.OwnershipLost)]
    public async Task RunShardAsync_WhenRescheduleIsNotApplied_DoesNotRecordReschedule(DurableJobMutationResult mutationResult)
    {
        var options = CreateOptions(maxConcurrentJobs: 1);
        var shard = CreateJobShard(CreateJobs(1));
        shard.RescheduleJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(mutationResult);
        var grainFactory = CreateGrainFactory();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobRunResult.RescheduleAt(DateTimeOffset.UtcNow.AddMinutes(1)));
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var services = new ServiceCollection();
        services.AddMetrics();
        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new DurableJobsInstruments(new OrleansInstruments(meterFactory));
        using var rescheduled = new MetricCollector<long>(
            meterFactory,
            "Microsoft.Orleans",
            "orleans-durablejobs-jobs-rescheduled");
        var executor = new ShardExecutor(
            grainFactory,
            options,
            CreateOverloadDetector(isOverloaded: false),
            NullLogger<ShardExecutor>.Instance,
            durableJobsInstruments: instruments);

        await executor.RunShardAsync(shard, CancellationToken.None);

        await shard.Received(1).RescheduleJobAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        Assert.Empty(rescheduled.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task RunShardAsync_UnknownStatusFlowsThroughFailureRetryPolicy()
    {
        Exception? retryException = null;
        var retryTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var options = CreateOptions(
            maxConcurrentJobs: 1,
            shouldRetry: (_, exception) =>
            {
                retryException = exception;
                return retryTime;
            });
        var shard = CreateJobShard(CreateJobs(1));
        var grainFactory = CreateGrainFactory();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult((DurableJobRunStatus)99));
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var executor = new ShardExecutor(
            grainFactory,
            options,
            CreateOverloadDetector(isOverloaded: false),
            NullLogger<ShardExecutor>.Instance);

        await executor.RunShardAsync(shard, CancellationToken.None);

        Assert.IsType<InvalidOperationException>(retryException);
        Assert.Contains("unsupported status value 99", retryException.Message, StringComparison.Ordinal);
        await shard.Received(1).RetryJobLaterAsync(Arg.Any<IJobRunContext>(), retryTime, Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RescheduleJobAsync(
            Arg.Any<IJobRunContext>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static DurableJobRunResult CreateResult(DurableJobRunStatus status) =>
        (DurableJobRunResult)Activator.CreateInstance(
            typeof(DurableJobRunResult),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [status, null, null, null],
            culture: null)!;

    [Fact]
    public async Task RunShardAsync_WithSlowStart_GraduallyIncreasesConcurrency()
    {
        var initialConcurrency = 2;
        var maxConcurrency = 16;
        var options = CreateOptions(
            maxConcurrentJobs: maxConcurrency,
            concurrencySlowStartEnabled: true,
            slowStartInitialConcurrency: initialConcurrency,
            slowStartInterval: TimeSpan.FromMilliseconds(100));
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var currentConcurrent = 0;
        var maxObservedConcurrent = 0;
        var concurrentLock = new object();
        var releaseJobs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrencyIncreased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Enough jobs to exercise the slow start ramp-up
        var jobs = CreateJobs(20);
        var shard = CreateJobShard(jobs);

        ConfigureGrainFactoryWithSlowJobExecution(grainFactory, async () =>
        {
            lock (concurrentLock)
            {
                currentConcurrent++;
                if (currentConcurrent > maxObservedConcurrent)
                {
                    maxObservedConcurrent = currentConcurrent;
                    if (maxObservedConcurrent > initialConcurrency)
                    {
                        concurrencyIncreased.TrySetResult();
                    }
                }
            }

            await releaseJobs.Task;

            lock (concurrentLock)
            {
                currentConcurrent--;
            }
        });

        var runTask = executor.RunShardAsync(shard, CancellationToken.None);
        try
        {
            var completedTask = await Task.WhenAny(
                concurrencyIncreased.Task,
                Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(concurrencyIncreased.Task, completedTask);
        }
        finally
        {
            releaseJobs.TrySetResult();
            await runTask;
        }

        // Slow start should limit initial concurrency, then ramp up
        Assert.True(maxObservedConcurrent <= maxConcurrency,
            $"Max concurrent jobs was {maxObservedConcurrent}, but limit was {maxConcurrency}");
        Assert.True(maxObservedConcurrent > initialConcurrency,
            $"Expected concurrency to increase beyond initial {initialConcurrency}, but max observed was {maxObservedConcurrent}");
    }

    [Fact]
    public async Task RunShardAsync_WithSlowStartDisabled_UsesFullConcurrencyImmediately()
    {
        var maxConcurrency = 5;
        var options = CreateOptions(
            maxConcurrentJobs: maxConcurrency,
            concurrencySlowStartEnabled: false);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var currentConcurrent = 0;
        var maxObservedConcurrent = 0;
        var concurrentLock = new object();

        var jobs = CreateJobs(10);
        var shard = CreateJobShard(jobs);

        ConfigureGrainFactoryWithSlowJobExecution(grainFactory, async () =>
        {
            lock (concurrentLock)
            {
                currentConcurrent++;
                if (currentConcurrent > maxObservedConcurrent)
                {
                    maxObservedConcurrent = currentConcurrent;
                }
            }

            await Task.Delay(100);

            lock (concurrentLock)
            {
                currentConcurrent--;
            }
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Without slow start, all concurrency slots should be available immediately
        Assert.True(maxObservedConcurrent <= maxConcurrency,
            $"Max concurrent jobs was {maxObservedConcurrent}, but limit was {maxConcurrency}");
        Assert.Equal(maxConcurrency, maxObservedConcurrent);
    }

    [Fact]
    public void ValidateConfiguration_WithSlowStartDisabled_AllowsNonPositiveInitialConcurrency()
    {
        var options = Options.Create(new DurableJobsOptions
        {
            ConcurrencySlowStartEnabled = false,
            SlowStartInitialConcurrency = 0
        });
        var validator = new Orleans.Hosting.DurableJobsOptionsValidator(
            NullLogger<Orleans.Hosting.DurableJobsOptionsValidator>.Instance,
            options);

        var exception = Record.Exception(validator.ValidateConfiguration);

        Assert.Null(exception);
    }

    [Fact]
    public async Task RunShardAsync_WhenJobFailsWithNoRetryPolicy_RecordsFailureWithoutRetryingReschedulingOrRemoving()
    {
        // Default ShouldRetry (CreateOptions default) always returns null, i.e. no retry: this is the
        // terminal-failure branch (ExecuteJobAsync's "failureException is not null && retryTime is null" path)
        // which none of the other tests exercise (they all configure ShouldRetry to return a retry time).
        var services = new ServiceCollection();
        services.AddMetrics();
        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new DurableJobsInstruments(new OrleansInstruments(meterFactory));
        using var failedCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-jobs-failed");
        using var retriedCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-jobs-retried");
        using var completedCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-jobs-completed");

        var options = CreateOptions(maxConcurrentJobs: 10);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(1);
        var shard = CreateJobShard(jobs);

        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        var terminalException = new InvalidOperationException("Simulated permanent job failure");
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobRunResult.Failed(terminalException));
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);

        var executor = new ShardExecutor(
            grainFactory,
            options,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider: null,
            durableJobsInstruments: instruments);

        await executor.RunShardAsync(shard, CancellationToken.None);

        // No follow-up action should be taken for a terminal (non-retryable) failure.
        await shard.DidNotReceive().RetryJobLaterAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RescheduleJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await shard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Exactly one failure metric recorded, and none of the completed/retried metrics fired -
        // a mutation that routed this branch through OnJobCompleted/OnJobRetried instead of OnJobFailed
        // would be caught here.
        Assert.Equal(1, Assert.Single(failedCollector.GetMeasurementSnapshot()).Value);
        Assert.Empty(retriedCollector.GetMeasurementSnapshot());
        Assert.Empty(completedCollector.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task SlowStartRampUpAsync_WhenTimerThrows_RecoversByReleasingFullConcurrencyImmediately()
    {
        // Covers the catch(Exception) branch in SlowStartRampUpAsync: if the ramp-up delay fails for any
        // reason, all remaining capacity must be released immediately (a single jump to full concurrency)
        // instead of leaving the shard stuck at the low initial concurrency forever.
        var initialConcurrency = 2;
        var maxConcurrency = 6;
        var options = CreateOptions(
            maxConcurrentJobs: maxConcurrency,
            concurrencySlowStartEnabled: true,
            slowStartInitialConcurrency: initialConcurrency,
            slowStartInterval: TimeSpan.FromMilliseconds(5));
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var grainFactory = Substitute.For<IInternalGrainFactory>();

        var holdGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedFullConcurrency = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentConcurrent = 0;
        var maxObservedConcurrent = 0;
        var concurrentLock = new object();

        // Enough jobs to occupy every permit simultaneously once the ramp-up releases full capacity.
        var jobs = CreateJobs(maxConcurrency + 4);
        var shard = CreateJobShard(jobs);

        ConfigureGrainFactoryWithSlowJobExecution(grainFactory, async () =>
        {
            lock (concurrentLock)
            {
                currentConcurrent++;
                if (currentConcurrent > maxObservedConcurrent)
                {
                    maxObservedConcurrent = currentConcurrent;
                    if (maxObservedConcurrent >= maxConcurrency)
                    {
                        reachedFullConcurrency.TrySetResult();
                    }
                }
            }

            await holdGate.Task;

            lock (concurrentLock)
            {
                currentConcurrent--;
            }
        });

        var throwingTimeProvider = new ThrowingTimerTimeProvider();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance, throwingTimeProvider);

        var runTask = executor.RunShardAsync(shard, CancellationToken.None);
        try
        {
            var completedTask = await Task.WhenAny(
                reachedFullConcurrency.Task,
                Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(reachedFullConcurrency.Task, completedTask);
        }
        finally
        {
            holdGate.TrySetResult();
            await runTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        // Despite the initial concurrency being capped at 2, the failed ramp-up must have released the
        // full remaining capacity so the shard is not permanently stuck at the low initial value.
        Assert.Equal(maxConcurrency, maxObservedConcurrent);

        // The shard must still make forward progress after recovering from the ramp-up failure.
        await shard.Received(maxConcurrency + 4).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Helper methods

    private static IOptions<DurableJobsOptions> CreateOptions(
        int maxConcurrentJobs = 10,
        TimeSpan? overloadBackoffDelay = null,
        Func<IJobRunContext, Exception, DateTimeOffset?>? shouldRetry = null,
        bool concurrencySlowStartEnabled = false,
        int? slowStartInitialConcurrency = null,
        TimeSpan? slowStartInterval = null)
    {
        var options = new DurableJobsOptions
        {
            MaxConcurrentJobsPerSilo = maxConcurrentJobs,
            OverloadBackoffDelay = overloadBackoffDelay ?? TimeSpan.FromMilliseconds(100),
            ShouldRetry = shouldRetry ?? ((_, _) => null), // Default: no retry
            ConcurrencySlowStartEnabled = concurrencySlowStartEnabled,
            SlowStartInitialConcurrency = slowStartInitialConcurrency ?? Environment.ProcessorCount,
            SlowStartInterval = slowStartInterval ?? TimeSpan.FromSeconds(10)
        };
        return Options.Create(options);
    }

    private static IOverloadDetector CreateOverloadDetector(bool isOverloaded)
    {
        var detector = Substitute.For<IOverloadDetector>();
        detector.IsOverloaded.Returns(isOverloaded);
        return detector;
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose <see cref="CreateTimer"/> always throws, used to deterministically
    /// simulate a failure in the middle of an <c>await Task.Delay(..., TimeProvider, ...)</c> call without
    /// relying on wall-clock timing.
    /// </summary>
    private sealed class ThrowingTimerTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("Simulated timer failure");
    }
    private static List<DurableJob> CreateJobs(int count, DateTimeOffset? dueTime = null)
    {
        var jobs = new List<DurableJob>();
        var baseDueTime = dueTime ?? DateTimeOffset.UtcNow.AddMilliseconds(-100);

        for (int i = 0; i < count; i++)
        {
            jobs.Add(new DurableJob
            {
                Id = $"job-{i}",
                Name = $"job-{i}",
                DueTime = baseDueTime.AddMilliseconds(i * 10),
                TargetGrainId = GrainId.Create("test", $"grain-{i}"),
                ShardId = "shard-1",
                Metadata = null
            });
        }

        return jobs;
    }

    private static IJobShard CreateJobShard(
        List<DurableJob> jobs,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        TaskCompletionSource? firstJobRegistered = null)
    {
        var shard = Substitute.For<IJobShard>();
        shard.Id.Returns("shard-1");
        shard.StartTime.Returns(startTime ?? DateTimeOffset.UtcNow.AddMinutes(-10));
        shard.EndTime.Returns(endTime ?? DateTimeOffset.UtcNow.AddMinutes(10));

        shard.ConsumeDurableJobsAsync().Returns(callInfo => CreateJobContexts(jobs, firstJobRegistered));

        shard.RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DurableJobMutationResult.Applied));
        shard.RetryJobLaterAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DurableJobMutationResult.Applied));
        shard.RescheduleJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DurableJobMutationResult.Applied));

        return shard;
    }

    private static IJobShard CreateJobShardWithDelayedYield(int jobCount, TimeSpan yieldDelay)
    {
        var jobs = CreateJobs(jobCount);
        var shard = Substitute.For<IJobShard>();
        shard.Id.Returns("shard-1");
        shard.StartTime.Returns(DateTimeOffset.UtcNow.AddMinutes(-10));
        shard.EndTime.Returns(DateTimeOffset.UtcNow.AddMinutes(10));

        shard.ConsumeDurableJobsAsync().Returns(callInfo => CreateJobContextsWithDelay(jobs, yieldDelay));

        shard.RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DurableJobMutationResult.Applied));
        shard.RetryJobLaterAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DurableJobMutationResult.Applied));
        shard.RescheduleJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DurableJobMutationResult.Applied));

        return shard;
    }

    private static async IAsyncEnumerable<IJobRunContext> CreateJobContexts(
        List<DurableJob> jobs,
        TaskCompletionSource? firstJobRegistered = null)
    {
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var context = Substitute.For<IJobRunContext>();
            context.Job.Returns(job);
            context.RunId.Returns(Guid.NewGuid().ToString());
            context.DequeueCount.Returns(1);
            yield return context;

            // The iterator resumes only after RunShardAsync has registered the yielded job task.
            if (i == 0)
            {
                firstJobRegistered?.TrySetResult();
            }
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<IJobRunContext> CreateJobContextsWithDelay(
        List<DurableJob> jobs,
        TimeSpan delay)
    {
        foreach (var job in jobs)
        {
            await Task.Delay(delay);

            var context = Substitute.For<IJobRunContext>();
            context.Job.Returns(job);
            context.RunId.Returns(Guid.NewGuid().ToString());
            context.DequeueCount.Returns(1);
            yield return context;
        }
    }

    private static IInternalGrainFactory CreateGrainFactory()
    {
        var factory = Substitute.For<IInternalGrainFactory>();

        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(DurableJobRunResult.Completed);

        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);

        return factory;
    }

    private static void ConfigureGrainFactoryToTrackCompletions(
        IInternalGrainFactory factory,
        List<string> completedJobs)
    {
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var context = callInfo.ArgAt<IJobRunContext>(0);
                lock (completedJobs)
                {
                    completedJobs.Add(context.Job.Id);
                }
                return DurableJobRunResult.Completed;
            });

        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
    }

    private static void ConfigureGrainFactoryWithSlowJobExecution(
        IInternalGrainFactory factory,
        Func<Task> executionAction)
    {
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns((Func<NSubstitute.Core.CallInfo, ValueTask<DurableJobRunResult>>)(async callInfo =>
            {
                await executionAction();
                return DurableJobRunResult.Completed;
            }));

        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
    }

    private static void ConfigureGrainFactoryWithSelectiveFailures(
        IInternalGrainFactory factory,
        List<string> completedJobs,
        List<string> failedJobs,
        ref int jobExecutionCount)
    {
        var executionCount = jobExecutionCount;

        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var context = callInfo.ArgAt<IJobRunContext>(0);
                var currentExecution = Interlocked.Increment(ref executionCount);

                // First job fails
                if (currentExecution == 1)
                {
                    lock (failedJobs)
                    {
                        failedJobs.Add(context.Job.Id);
                    }
                    var exception = new InvalidOperationException("Simulated job failure");
                    return DurableJobRunResult.Failed(exception);
                }

                // Other jobs succeed
                lock (completedJobs)
                {
                    completedJobs.Add(context.Job.Id);
                }
                return DurableJobRunResult.Completed;
            });

        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);

        jobExecutionCount = executionCount;
    }

    private static IInternalGrainFactory CreateGrainFactoryWithCanceledExecution()
    {
        var factory = Substitute.For<IInternalGrainFactory>();

        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromCanceled<DurableJobRunResult>(new CancellationToken(canceled: true)));

        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);

        return factory;
    }

    private static (IInternalGrainFactory, StrongBox<int>) CreateGrainFactoryWithPollingBehavior()
    {
        var factory = Substitute.For<IInternalGrainFactory>();
        var callBox = new StrongBox<int>(0);

        var extension = Substitute.For<IDurableJobReceiverExtension>();

        // First 3 calls return InProgress, 4th returns Completed
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var currentCall = Interlocked.Increment(ref callBox.Value);
                if (currentCall < 4)
                {
                    return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
                }
                return DurableJobRunResult.Completed;
            });

        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);

        return (factory, callBox);
    }

    private static (IInternalGrainFactory, StrongBox<int>) CreateGrainFactoryWithPollingThenFailure()
    {
        var factory = Substitute.For<IInternalGrainFactory>();
        var callBox = new StrongBox<int>(0);

        var extension = Substitute.For<IDurableJobReceiverExtension>();

        // First 3 calls return InProgress, 4th returns Failed
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var currentCall = Interlocked.Increment(ref callBox.Value);
                if (currentCall < 4)
                {
                    return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
                }
                var exception = new InvalidOperationException("Job failed after polling");
                return DurableJobRunResult.Failed(exception);
            });

        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);

        return (factory, callBox);
    }

    private static (IInternalGrainFactory, StrongBox<int>) CreateGrainFactoryWithTimedPolling(
        int pollDelayMs,
        List<DateTimeOffset> pollTimestamps)
    {
        var factory = Substitute.For<IInternalGrainFactory>();
        var callBox = new StrongBox<int>(0);

        var extension = Substitute.For<IDurableJobReceiverExtension>();

        // First 3 calls return InProgress (recording timestamps after the initial call), 4th returns Completed
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var currentCall = Interlocked.Increment(ref callBox.Value);
                if (currentCall > 1)
                {
                    lock (pollTimestamps)
                    {
                        pollTimestamps.Add(DateTimeOffset.UtcNow);
                    }
                }

                if (currentCall < 4)
                {
                    return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(pollDelayMs));
                }
                return DurableJobRunResult.Completed;
            });

        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);

        return (factory, callBox);
    }

    private sealed class TimerTrackingFakeTimeProvider(DateTimeOffset startDateTime) : FakeTimeProvider(startDateTime)
    {
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _timerCreated.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timerCreated.TrySetResult();
            return timer;
        }
    }
}
