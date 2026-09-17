#nullable enable
#pragma warning disable ORLEANSEXP005

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
[TestCategory("BVT"), TestCategory("DurableJobs")]
public class LocalDurableJobManagerTests
{
    [Fact]
    public async Task Stop_WhenRequestCancellationCallbacksThrow_DrainsRequestsAndCleansUp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("callback-failure", start, start.AddHours(1));
        var failure = new InvalidOperationException("Provider cancellation callback failed.");
        var schedulingStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removalStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeScheduling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        var expectedJob = new DurableJob
        {
            Id = "job-1",
            Name = "job",
            DueTime = start,
            TargetGrainId = GrainId.Create("test", "job"),
            ShardId = shard.Id
        };
        shard.ScheduleJob = (_, token) => RunProviderAsync<DurableJob?>(token, schedulingStarted, resumeScheduling, expectedJob);
        shard.RemoveJob = (_, token) => RunProviderAsync(token, removalStarted, resumeRemoval, DurableJobMutationResult.Applied);
        var shardManager = new TestJobShardManager();
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var manager = CreateManager(shardManager, timeProvider, CreateOptions(), logger: logger);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        accessor.AddWritableShard(start, shard);
        var scheduling = manager.ScheduleJobAsync(CreateScheduleRequest(start), cancellationToken);
        var removal = manager.CancelAsync(expectedJob, cancellationToken);
        var tokens = await Task.WhenAll(schedulingStarted.Task, removalStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var stop = observer.OnStop(cancellationToken);
        try
        {
            Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
            Assert.Equal(2, Volatile.Read(ref notifications));
            Assert.False(stop.IsCompleted);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);
            Assert.Empty(shardManager.UnregisteredShards);

            resumeScheduling.SetResult();
            Assert.Same(expectedJob, await scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.False(stop.IsCompleted);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);
            Assert.Empty(shardManager.UnregisteredShards);

            resumeRemoval.SetResult();
            Assert.True(await removal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        }
        finally
        {
            resumeScheduling.TrySetResult();
            resumeRemoval.TrySetResult();
            shard.AllowDispose.TrySetResult();
            await Task.WhenAll(scheduling, removal).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shard.DisposeCallCount);
        AssertSchedulingCacheEmpty(accessor);
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        var aggregate = Assert.IsType<AggregateException>(error.Exception).Flatten();
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.All(aggregate.InnerExceptions, exception => Assert.Same(failure, exception));

        async Task<T> RunProviderAsync<T>(
            CancellationToken token,
            TaskCompletionSource<CancellationToken> started,
            TaskCompletionSource resume,
            T result)
        {
            using var notification = token.Register(() => Interlocked.Increment(ref notifications));
            using var throwingCallback = token.Register(() => throw failure);
            started.SetResult(token);
            await resume.Task;
            return result;
        }
    }

    [Fact]
    public async Task Stop_WhenExecutionCancellationCallbackThrows_AwaitsExecutionAndCleansUp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = timeProvider.GetUtcNow();
        var activeDisposal = new BlockingQueueShard("active-callback-failure", start, start.AddHours(1));
        var inactive = new BlockingQueueShard("inactive-after-callback-failure", start.AddHours(1), start.AddHours(2));
        var failure = new InvalidOperationException("Execution cancellation callback failed.");
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shard = CreateSubstituteShard(activeDisposal.Id, activeDisposal.StartTime, activeDisposal.EndTime);
        shard.ConsumeDurableJobsAsync().Returns(_ => ConsumeAsync());
        shard.DisposeAsync().Returns(_ => activeDisposal.DisposeAsync());
        var shardManager = new TestJobShardManager();
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var manager = CreateManager(shardManager, timeProvider, CreateOptions(), logger: logger);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        accessor.AddWritableShard(start, shard);
        accessor.AddWritableShard(inactive.StartTime, inactive);
        accessor.TryActivateShard(shard);
        var executionToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var stop = observer.OnStop(cancellationToken);
        try
        {
            Assert.True(executionToken.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            Assert.False(activeDisposal.DisposeStarted.Task.IsCompleted);
            Assert.False(inactive.DisposeStarted.Task.IsCompleted);

            resumeExecution.SetResult();
            await activeDisposal.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.False(inactive.DisposeStarted.Task.IsCompleted);
            activeDisposal.AllowDispose.SetResult();
            await inactive.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            resumeExecution.TrySetResult();
            activeDisposal.AllowDispose.TrySetResult();
            inactive.AllowDispose.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, activeDisposal.DisposeCallCount);
        Assert.Equal(1, inactive.DisposeCallCount);
        Assert.Contains(inactive, shardManager.UnregisteredShards);
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        AssertSchedulingCacheEmpty(accessor);
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        var aggregate = Assert.IsType<AggregateException>(error.Exception).Flatten();
        Assert.Same(failure, Assert.Single(aggregate.InnerExceptions));

        async IAsyncEnumerable<IJobRunContext> ConsumeAsync([EnumeratorCancellation] CancellationToken token = default)
        {
            using var registration = token.Register(() => throw failure);
            started.SetResult(token);
            await resumeExecution.Task;
            token.ThrowIfCancellationRequested();
            yield break;
        }
    }

    [Fact]
    public async Task PeriodicDiscovery_ActivatesFirstYieldBeforeSweepCompletesAndDisposesOnStop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shardManager = new TestJobShardManager();
        var shard = new BlockingQueueShard("first-yield", timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddHours(1));
        shard.AllowDispose.SetResult();
        var tailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryCanceled = false;
        var disposalCount = 0;
        var calls = 0;
        shardManager.DiscoverShards = (_, _, token) => DiscoverShards(token);

        async IAsyncEnumerable<IJobShard> DiscoverShards([EnumeratorCancellation] CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            try
            {
                yield return shard;
                tailStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                discoveryCanceled = token.IsCancellationRequested;
                Interlocked.Increment(ref disposalCount);
            }
        }

        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        manager.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        try
        {
            accessor.SignalShardCheck();
            await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await tailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(0, Volatile.Read(ref disposalCount));
            Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var running));
            Assert.NotNull(running);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            await lifecycle.OnStop(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.True(discoveryCanceled);
        Assert.Equal(1, Volatile.Read(ref disposalCount));
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task PeriodicDiscovery_RetainsMembershipSignalDuringSweepAndRestartsOnTimer(int? checkIntervalMinutes)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shardManager = new TestJobShardManager();
        var firstSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var membershipSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var periodicSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateOptions();
        if (checkIntervalMinutes is { } minutes)
        {
            options.ShardCheckInterval = TimeSpan.FromMinutes(minutes);
        }
        else
        {
            Assert.Equal(TimeSpan.FromMinutes(5), options.ShardCheckInterval);
        }

        var calls = 0;
        var disposedSweeps = 0;
        shardManager.DiscoverShards = (_, _, token) => DiscoverShards(token);

        async IAsyncEnumerable<IJobShard> DiscoverShards([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                switch (Interlocked.Increment(ref calls))
                {
                    case 1:
                        firstSweep.SetResult();
                        await finishSweep.Task.WaitAsync(token);
                        break;
                    case 2:
                        membershipSweep.SetResult();
                        break;
                    case 3:
                        periodicSweep.SetResult();
                        break;
                }

                yield break;
            }
            finally
            {
                Interlocked.Increment(ref disposedSweeps);
            }
        }

        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        manager.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        try
        {
            accessor.SignalShardCheck();
            await firstSweep.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(0, Volatile.Read(ref disposedSweeps));
            accessor.SignalShardCheck();
            finishSweep.SetResult();
            await membershipSweep.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await manager.QueueTask(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal(2, Volatile.Read(ref disposedSweeps));

            timeProvider.Advance(options.ShardCheckInterval - TimeSpan.FromTicks(1));
            await manager.QueueTask(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(periodicSweep.Task.IsCompleted);
            Assert.Equal(2, Volatile.Read(ref calls));

            timeProvider.Advance(TimeSpan.FromTicks(1));
            await periodicSweep.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(3, Volatile.Read(ref calls));
        }
        finally
        {
            await lifecycle.OnStop(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(23)]
    public async Task Discovery_UsesConfiguredLookahead(int? lookaheadMinutes)
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shardManager = new TestJobShardManager();
        var options = CreateOptions();
        if (lookaheadMinutes is { } minutes)
        {
            options.ShardLoadLookaheadPeriod = TimeSpan.FromMinutes(minutes);
        }

        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var expectedLookahead = TimeSpan.FromMinutes(lookaheadMinutes ?? 10);

        await accessor.ProcessShardCheckCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(timeProvider.GetUtcNow().Add(expectedLookahead), shardManager.LastMaxDueTime);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await accessor.ProcessShardCheckCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(timeProvider.GetUtcNow().Add(expectedLookahead), shardManager.LastMaxDueTime);
    }

    [Fact]
    public async Task Discovery_MaximumLookaheadIncludesAllRepresentableDates()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shardManager = new TestJobShardManager();
        var options = CreateOptions();
        options.ShardLoadLookaheadPeriod = TimeSpan.MaxValue;
        new DurableJobsOptionsValidator(NullLogger<DurableJobsOptionsValidator>.Instance, Options.Create(options)).ValidateConfiguration();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);

        await accessor.ProcessShardCheckCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DateTimeOffset.MaxValue, shardManager.LastMaxDueTime);
        Assert.Equal(TimeSpan.Zero, shardManager.LastMaxDueTime.Offset);
        Assert.Equal(TimeSpan.MaxValue, options.ShardLoadLookaheadPeriod);
    }

    [Theory]
    [InlineData(2, 0, 2)]
    [InlineData(2, 1, 1)]
    [InlineData(2, 2, 0)]
    [InlineData(2, 3, 0)]
    [InlineData(2, long.MaxValue, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 0)]
    public async Task Discovery_NearMaximumTimeClampsLookahead(long remainingTicks, long lookaheadTicks, long expectedRemainingTicks)
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.MaxValue.AddTicks(-remainingTicks));
        var shardManager = new TestJobShardManager();
        var options = CreateOptions();
        options.ShardLoadLookaheadPeriod = TimeSpan.FromTicks(lookaheadTicks);
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);

        await accessor.ProcessShardCheckCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DateTimeOffset.MaxValue.AddTicks(-expectedRemainingTicks), shardManager.LastMaxDueTime);
        Assert.Equal(TimeSpan.Zero, shardManager.LastMaxDueTime.Offset);
        Assert.Equal(TimeSpan.FromTicks(lookaheadTicks), options.ShardLoadLookaheadPeriod);
    }

    [Fact]
    public async Task Discovery_RevisitedFutureShardActivatesAfterClockAdvances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shardManager = new TestJobShardManager();
        var start = timeProvider.GetUtcNow().AddMinutes(10);
        var shard = new CompletingShard("future", start, start.AddHours(1));
        shardManager.AssignedShards.Add(shard);
        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);

        await accessor.ProcessShardCheckCycleAsync(cancellationToken);
        Assert.True(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        Assert.Equal(timeProvider.GetUtcNow().AddMinutes(10), shardManager.LastMaxDueTime);

        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await accessor.ProcessShardCheckCycleAsync(cancellationToken);
        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var running));
        Assert.Equal(timeProvider.GetUtcNow().AddMinutes(10), shardManager.LastMaxDueTime);
        await shard.MarkAsCompleteAsync(cancellationToken);
        await running!.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Contains(shard, shardManager.UnregisteredShards);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task Discovery_CancellationAfterYieldTracksShardUntilShutdownCompletes(int startDelayMinutes)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = timeProvider.GetUtcNow().AddMinutes(startDelayMinutes);
        var shard = new BlockingQueueShard("canceled-yield", start, start.AddHours(1));
        var discoveryDisposed = false;
        var shardManager = new TestJobShardManager
        {
            DiscoverShards = (_, _, _) => DiscoverShards()
        };

        async IAsyncEnumerable<IJobShard> DiscoverShards()
        {
            await Task.CompletedTask;
            try
            {
                cancellation.Cancel();
                yield return shard;
            }
            finally
            {
                discoveryDisposed = true;
            }
        }

        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        manager.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        Task? stop = null;
        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => accessor.ProcessShardCheckCycleAsync(cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(discoveryDisposed);
            Assert.True(accessor.HasCachedShard(shard.Id));
            Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
            Assert.False(shard.ConsumeStarted.Task.IsCompleted);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);

            stop = lifecycle.OnStop(cancellationToken);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            await (stop ?? lifecycle.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(shard.ConsumeStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task Discovery_CanceledRepeatedYieldKeepsRunningShardUntilShutdown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = timeProvider.GetUtcNow();
        var shard = new BlockingQueueShard("running-yield", start, start.AddHours(1));
        var shardManager = new TestJobShardManager { DiscoverShards = (_, _, _) => DiscoverShards() };

        async IAsyncEnumerable<IJobShard> DiscoverShards()
        {
            await Task.CompletedTask;
            cancellation.Cancel();
            yield return shard;
        }

        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        manager.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        Task? stop = null;
        try
        {
            accessor.AddWritableShard(start, shard);
            accessor.TryActivateShard(shard);
            await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var running));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => accessor.ProcessShardCheckCycleAsync(cancellation.Token));
            Assert.True(accessor.HasCachedShard(shard.Id));
            Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var afterDiscovery));
            Assert.Same(running, afterDiscovery);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);

            stop = lifecycle.OnStop(cancellationToken);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            await (stop ?? lifecycle.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.HasWritableShard(start));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stop_DuringDiscoveryReadinessCheckRejectsActivationAndAwaitsCleanup(bool failDiscoveryDisposal, bool useSiloLifecycle)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycleLogger = new RecordingLogger<SiloLifecycleSubject>();
        ILifecycleObserver observer;
        if (useSiloLifecycle)
        {
            var lifecycle = new SiloLifecycleSubject(lifecycleLogger);
            manager.Participate(lifecycle);
            observer = lifecycle;
        }
        else
        {
            observer = CreateLifecycleObserver(manager);
        }

        var shard = new BlockingQueueShard("final-activation", timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddHours(1));
        var discoveredShard = Substitute.For<IJobShard>();
        Task? stop = null;
        discoveredShard.Id.Returns(shard.Id);
        discoveredShard.StartTime.Returns(_ =>
        {
            // The yielded shard has passed its cancellation check, but has not been activated yet.
            stop ??= observer.OnStop(cancellationToken);
            // OnStop returns its task so this check can finish while shutdown awaits the loop.
            return shard.StartTime;
        });
        discoveredShard.EndTime.Returns(shard.EndTime);
        discoveredShard.ConsumeDurableJobsAsync().Returns(_ => shard.ConsumeDurableJobsAsync());
        discoveredShard.DisposeAsync().Returns(_ => shard.DisposeAsync());
        var undeliveredShard = CreateSubstituteShard("canceled-after-stop", shard.StartTime, shard.EndTime);
        var discoveryDisposed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalCount = 0;
        shardManager.DiscoverShards = (_, _, _) => DiscoverShards();

        async IAsyncEnumerable<IJobShard> DiscoverShards()
        {
            await Task.CompletedTask;
            try
            {
                yield return discoveredShard;
                // Cancellation at delivery leaves the iterator suspended, forcing cleanup through DisposeAsync.
                yield return undeliveredShard;
            }
            finally
            {
                Interlocked.Increment(ref disposalCount);
                // Let the readiness check finish before observing the closed activation gate.
                discoveryDisposed.SetResult(manager.QueueTask(() =>
                {
                    Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
                    Assert.False(shard.ConsumeStarted.Task.IsCompleted);
                    Assert.NotNull(stop);
                    Assert.False(stop.IsCompleted);
                    return Task.CompletedTask;
                }));
                if (failDiscoveryDisposal)
                {
                    throw new InvalidOperationException("Discovery disposal failed");
                }
            }
        }

        await observer.OnStart(cancellationToken);
        try
        {
            accessor.SignalShardCheck();
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var shutdownAssertion = await discoveryDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await shutdownAssertion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(0, shard.DisposeCallCount);
            Assert.True(accessor.HasCachedShard(undeliveredShard.Id));
            Assert.False(accessor.TryGetRunningShardTask(undeliveredShard.Id, out _));
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            if (stop is not null)
            {
                await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

        }

        Assert.Equal(1, Volatile.Read(ref disposalCount));
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Equal(2, shardManager.UnregisteredShards.Count);
        Assert.Contains(discoveredShard, shardManager.UnregisteredShards);
        Assert.Contains(undeliveredShard, shardManager.UnregisteredShards);
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.HasCachedShard(undeliveredShard.Id));
        await undeliveredShard.Received(1).DisposeAsync();
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        Assert.DoesNotContain(lifecycleLogger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Stop_DisposesInactiveShardsWhenRunningShardFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = timeProvider.GetUtcNow();
        var running = new FaultingOnCancellationShard("faulting-active", start, start.AddHours(1));
        var inactive = new BlockingQueueShard("inactive", start.AddMinutes(10), start.AddHours(1));
        var manager = CreateManager(new TestJobShardManager(), timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var logger = new RecordingLogger<SiloLifecycleSubject>();
        var lifecycle = new SiloLifecycleSubject(logger);
        manager.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        Task? stop = null;
        try
        {
            accessor.AddWritableShard(start, running);
            accessor.TryActivateShard(running);
            accessor.AddWritableShard(inactive.StartTime, inactive);
            await running.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            stop = lifecycle.OnStop(cancellationToken);
            await inactive.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            inactive.AllowDispose.TrySetResult();
            await (stop ?? lifecycle.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, running.DisposeCallCount);
        Assert.Equal(1, inactive.DisposeCallCount);
        Assert.False(inactive.ConsumeStarted.Task.IsCompleted);
        Assert.False(accessor.HasCachedShard(inactive.Id));
        Assert.False(accessor.HasWritableShard(inactive.StartTime));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error
            && entry.Exception is InvalidOperationException { Message: "Unexpected shard failure" });
    }

    [Fact]
    public async Task Stop_ClosesActivationGateBeforeShutdownCallbacks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var manager = CreateManager(new TestJobShardManager(), timeProvider, CreateOptions(), logger: logger);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycle = Substitute.For<ISiloLifecycle>();
        ILifecycleObserver? observer = null;
        lifecycle.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>())
            .Returns(call =>
            {
                observer = call.ArgAt<ILifecycleObserver>(2);
                return Substitute.For<IDisposable>();
            });
        manager.Participate(lifecycle);
        Assert.NotNull(observer);

        var shard = new BlockingQueueShard("after-shutdown-snapshot", timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddHours(1));
        accessor.AddWritableShard(shard.StartTime, shard);
        var activationCount = 0;
        logger.OnLog = eventId =>
        {
            if (eventId.Name == "LogStopping")
            {
                Interlocked.Increment(ref activationCount);
                accessor.TryActivateShard(shard);
            }
        };

        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        // Without OnStart, there are no loop tasks to await: OnStop reaches the final shard await synchronously.
        var stop = observer.OnStop(cancellationToken);
        try
        {
            Assert.Equal(1, Volatile.Read(ref activationCount));
            Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
            Assert.False(stop.IsCompleted);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(shard.ConsumeStarted.Task.IsCompleted);
            Assert.Equal(0, shard.DisposeCallCount);
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        accessor.TryActivateShard(shard);
        await manager.QueueTask(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(shard.ConsumeStarted.Task.IsCompleted);
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
    }

    [Fact]
    public async Task Stop_WhileActivationIsBeingPublished_AwaitsExecutionAndCleanup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var manager = CreateManager(new TestJobShardManager(), timeProvider, CreateOptions(), logger: logger);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycle = Substitute.For<ISiloLifecycle>();
        ILifecycleObserver? observer = null;
        lifecycle.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>())
            .Returns(call =>
            {
                observer = call.ArgAt<ILifecycleObserver>(2);
                return Substitute.For<IDisposable>();
            });
        manager.Participate(lifecycle);
        Assert.NotNull(observer);
        var shard = new BlockingQueueShard("in-flight-activation", timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddHours(1));
        accessor.AddWritableShard(shard.StartTime, shard);
        Task? registered = null;
        Task? stop = null;
        var registeredTaskWasCompleted = false;
        var disposedBeforeDispatch = false;
        logger.OnLog = eventId =>
        {
            if (eventId.Name == "LogStartingShard")
            {
                accessor.TryGetRunningShardTask(shard.Id, out registered);
                registeredTaskWasCompleted = registered?.IsCompleted == true;
                stop = observer.OnStop(cancellationToken);
                disposedBeforeDispatch = shard.DisposeStarted.Task.IsCompleted;
            }
        };

        accessor.TryActivateShard(shard);
        accessor.TryGetRunningShardTask(shard.Id, out var afterDispatch);
        try
        {
            Assert.NotNull(registered);
            Assert.NotNull(stop);
            Assert.False(registeredTaskWasCompleted);
            Assert.False(disposedBeforeDispatch);
            Assert.Same(registered, afterDispatch);
            Assert.False(stop.IsCompleted);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(registered.IsCompleted);
            Assert.Equal(0, shard.DisposeCallCount);
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            if (stop is not null)
            {
                await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            if (afterDispatch is not null)
            {
                await afterDispatch.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.HasWritableShard(shard.StartTime));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
    }

    [Fact]
    public async Task TryActivateShard_WhenAnotherActivationWinsReadinessRace_RunsShardOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var manager = CreateManager(new TestJobShardManager(), timeProvider, CreateOptions(), logger: logger);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        var shard = new BlockingQueueShard("competing-activation", timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddHours(1));
        var discoveredShard = Substitute.For<IJobShard>();
        var readinessChecks = 0;
        discoveredShard.Id.Returns(shard.Id);
        discoveredShard.StartTime.Returns(_ =>
        {
            if (Interlocked.Increment(ref readinessChecks) == 1)
            {
                // Both attempts pass the initial running-shard check before either publishes its task.
                accessor.TryActivateShard(discoveredShard);
            }

            return shard.StartTime;
        });
        discoveredShard.EndTime.Returns(shard.EndTime);
        discoveredShard.ConsumeDurableJobsAsync().Returns(_ => shard.ConsumeDurableJobsAsync());
        discoveredShard.DisposeAsync().Returns(_ => shard.DisposeAsync());
        accessor.AddWritableShard(shard.StartTime, discoveredShard);

        Task? stop = null;
        try
        {
            accessor.TryActivateShard(discoveredShard);
            await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Single(logger.Entries, entry => entry.EventId.Name == "LogStartingShard");
            discoveredShard.Received(1).ConsumeDurableJobsAsync();
            Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var running));

            stop = observer.OnStop(cancellationToken);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.NotNull(running);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            await (stop ?? observer.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        AssertSchedulingCacheEmpty(accessor);
    }

    [Fact]
    public async Task Discovery_WhenLaterCandidateFails_LeavesYieldedPrefixRunning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shardManager = new TestJobShardManager();
        var shard = new BlockingQueueShard("successful-prefix", timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddHours(1));
        shard.AllowDispose.SetResult();
        var failure = new InvalidOperationException("Later candidate failed");
        var disposalCount = 0;
        shardManager.DiscoverShards = (_, _, token) => DiscoverShards(token);

        async IAsyncEnumerable<IJobShard> DiscoverShards([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return shard;
                await shard.ConsumeStarted.Task.WaitAsync(token);
                throw failure;
            }
            finally
            {
                Interlocked.Increment(ref disposalCount);
            }
        }

        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        manager.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => accessor.ProcessShardCheckCycleAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.Same(failure, exception);
            Assert.Equal(1, Volatile.Read(ref disposalCount));
            Assert.True(accessor.HasCachedShard(shard.Id));
            Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var running));
            Assert.NotNull(running);
            Assert.False(running.IsCompleted);
            Assert.Equal(0, shard.DisposeCallCount);
            Assert.Empty(shardManager.UnregisteredShards);
        }
        finally
        {
            await lifecycle.OnStop(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
    }

    [Fact]
    public async Task Stop_WhenActiveShardWaitsForQueueChange_CompletesAfterCleanupWithoutLifecycleError()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycleLogger = new RecordingLogger<SiloLifecycleSubject>();
        var lifecycle = new SiloLifecycleSubject(lifecycleLogger);
        var shardKey = timeProvider.GetUtcNow();
        var shard = new BlockingQueueShard("active-shard", shardKey, shardKey.Add(options.ShardDuration));

        manager.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        accessor.AddWritableShard(shardKey, shard);
        accessor.TryActivateShard(shard);

        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var runTask));
        Assert.False(runTask!.IsCompleted);

        var stopTask = lifecycle.OnStop(TestContext.Current.CancellationToken);
        await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(stopTask.IsCompleted);

        shard.AllowDispose.SetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        Assert.DoesNotContain(
            lifecycleLogger.Entries,
            entry => entry.Level == LogLevel.Error && entry.EventId.Id == (int)ErrorCode.LifecycleStartFailure);
    }

    [Fact]
    public async Task Stop_WhenActiveShardFails_ReportsLifecycleError()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var manager = CreateManager(new TestJobShardManager(), timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var lifecycleLogger = new RecordingLogger<SiloLifecycleSubject>();
        var lifecycle = new SiloLifecycleSubject(lifecycleLogger);
        var shardKey = timeProvider.GetUtcNow();
        var shard = new FaultingOnCancellationShard("faulting-shard", shardKey, shardKey.Add(options.ShardDuration));

        manager.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        accessor.AddWritableShard(shardKey, shard);
        accessor.TryActivateShard(shard);

        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await lifecycle.OnStop(TestContext.Current.CancellationToken).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Contains(
            lifecycleLogger.Entries,
            entry => entry.Level == LogLevel.Error
                && entry.EventId.Id == (int)ErrorCode.LifecycleStartFailure
                && entry.Exception is InvalidOperationException { Message: "Unexpected shard failure" });
    }

    [Fact]
    public async Task ProcessShardCheckCycleAsync_MarksExpiredWritableShardComplete()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var shard = CreateSubstituteShard("expired-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(accessor.HasWritableShard(shardKey));
        await shard.Received(1).MarkAsCompleteAsync(Arg.Any<CancellationToken>());
        Assert.Equal(timeProvider.GetUtcNow().AddMinutes(10), shardManager.LastMaxDueTime);
    }

    [Fact]
    public async Task ProcessShardCheckCycleAsync_MarksAllExpiredWritableShardStripesComplete()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardStripeCount = 2;
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var firstShard = CreateSubstituteShard("expired-shard-0", shardKey, shardKey.Add(options.ShardDuration));
        var secondShard = CreateSubstituteShard("expired-shard-1", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, firstShard, stripe: 0);
        accessor.AddWritableShard(shardKey, secondShard, stripe: 1);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(accessor.HasWritableShard(shardKey, stripe: 0));
        Assert.False(accessor.HasWritableShard(shardKey, stripe: 1));
        await firstShard.Received(1).MarkAsCompleteAsync(Arg.Any<CancellationToken>());
        await secondShard.Received(1).MarkAsCompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessShardCheckCycleAsync_LeavesNonExpiredWritableShardOpen()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var shard = CreateSubstituteShard("active-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.True(accessor.HasWritableShard(shardKey));
        await shard.DidNotReceive().MarkAsCompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpiredWritableShard_DrainsThenUnregistersAndDisposes()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var shard = new CompletingShard("draining-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);
        accessor.TryActivateShard(shard);

        Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var runTask));

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, shard.MarkAsCompleteCallCount);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
    }

    [Fact]
    public async Task AssignedShardActivation_UsesTimeProvider()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardActivationBufferPeriod = TimeSpan.FromMinutes(5);
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shard = new CompletingShard(
            "future-shard",
            timeProvider.GetUtcNow().AddMinutes(10),
            timeProvider.GetUtcNow().AddMinutes(11));
        shardManager.AssignedShards.Add(shard);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));

        timeProvider.Advance(TimeSpan.FromMinutes(11));

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var runTask));

        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await shard.MarkAsCompleteAsync(CancellationToken.None);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompletedWritableShardCleanup_RemovesWritableShard()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var shard = new CompletingShard("completed-writable-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);
        accessor.TryActivateShard(shard);

        Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var runTask));

        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await shard.MarkAsCompleteAsync(CancellationToken.None);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(accessor.HasWritableShard(shardKey));
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        Assert.Equal(1, shard.DisposeCallCount);
    }

    [Fact]
    public async Task CompletedWritableShardCleanup_DoesNotRemoveReplacementWritableShard()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var completedShard = new CompletingShard("completed-writable-shard", shardKey, shardKey.Add(options.ShardDuration));
        var replacementShard = CreateSubstituteShard("replacement-writable-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, completedShard);
        accessor.TryActivateShard(completedShard);

        Assert.True(accessor.TryGetRunningShardTask(completedShard.Id, out var runTask));

        await completedShard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        accessor.AddWritableShard(shardKey, replacementShard);
        await completedShard.MarkAsCompleteAsync(CancellationToken.None);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(accessor.TryGetWritableShard(shardKey, out var currentShard));
        Assert.Same(replacementShard, currentShard);
    }

    [Fact]
    public async Task ProcessShardCheckCycleAsync_WhenExpiredWritableShardIsDisposed_RemovesWritableShard()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var shard = new DisposedSchedulingShard("disposed-expired-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(accessor.HasWritableShard(shardKey));
    }

    [Fact]
    public async Task Stop_DuringShardCreation_CancelsQueuedSchedulingAndDisposesLateCreation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shard = new BlockingQueueShard("late-creation", timeProvider.GetUtcNow(), timeProvider.GetUtcNow().Add(options.ShardDuration));
        shard.ScheduleJob = (_, _) => throw new InvalidOperationException("A canceled creation must not schedule a job.");
        var creation = new TaskCompletionSource<IJobShard>(TaskCreationOptions.RunContinuationsAsynchronously);
        var createStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shardManager = new TestJobShardManager
        {
            CreateShard = (_, _, _, token) =>
            {
                createStarted.SetResult(token);
                return creation.Task;
            }
        };
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        var scheduling = manager.ScheduleJobAsync(CreateScheduleRequest(shard.StartTime), cancellationToken);
        var creationToken = await createStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var queued = manager.ScheduleJobAsync(CreateScheduleRequest(shard.EndTime), cancellationToken);
        Task? stop = null;
        try
        {
            Assert.False(queued.IsCompleted);
            Assert.Equal(1, shardManager.CreateShardCallCount);
            // No background loops: shutdown must wait specifically for the admitted scheduling calls.
            stop = observer.OnStop(cancellationToken);
            Assert.True(creationToken.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.False(scheduling.IsCompleted);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);

            // Providers can complete successfully despite cancellation; the returned resource is still ours.
            creation.SetResult(shard);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.False(shard.ConsumeStarted.Task.IsCompleted);
            Assert.Equal(0, shard.DisposeCallCount);
        }
        finally
        {
            creation.TrySetResult(shard);
            shard.AllowDispose.TrySetResult();
            await (stop ?? observer.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shardManager.CreateShardCallCount);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        AssertSchedulingCacheEmpty(accessor);
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(false, "failure")]
    [InlineData(false, "cancellation")]
    [InlineData(false, "full")]
    [InlineData(false, "disposed")]
    [InlineData(true, "success")]
    [InlineData(true, "failure")]
    [InlineData(true, "cancellation")]
    [InlineData(true, "full")]
    [InlineData(true, "disposed")]
    public async Task Stop_DuringExistingShardScheduling_AwaitsOutcomeBeforeDisposal(bool active, string outcome)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var start = active ? timeProvider.GetUtcNow() : timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("existing-scheduling", start, start.Add(options.ShardDuration));
        var scheduleStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduleResult = new TaskCompletionSource<DurableJob?>(TaskCreationOptions.RunContinuationsAsynchronously);
        shard.ScheduleJob = (_, token) =>
        {
            scheduleStarted.SetResult(token);
            return scheduleResult.Task;
        };
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        accessor.AddWritableShard(start, shard);
        if (active)
        {
            accessor.TryActivateShard(shard);
            await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        var request = CreateScheduleRequest(start);
        var expectedJob = new DurableJob
        {
            Id = "accepted-job",
            Name = request.JobName,
            DueTime = request.DueTime,
            TargetGrainId = request.Target,
            ShardId = shard.Id
        };
        var failure = new InvalidOperationException("Scheduling failed");
        var scheduling = manager.ScheduleJobAsync(request, cancellationToken);
        var schedulingToken = await scheduleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Task? stop = null;
        try
        {
            stop = observer.OnStop(cancellationToken);
            Assert.True(schedulingToken.IsCancellationRequested);
            await manager.QueueTask(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);
            Assert.True(accessor.HasCachedShard(shard.Id));
            Assert.True(accessor.HasWritableShard(start));

            switch (outcome)
            {
                case "success":
                    scheduleResult.SetResult(expectedJob);
                    Assert.Same(expectedJob, await scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                    break;
                case "failure":
                    scheduleResult.SetException(failure);
                    Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                        () => scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)));
                    break;
                case "cancellation":
                    scheduleResult.SetCanceled(schedulingToken);
                    var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                    Assert.Equal(schedulingToken, exception.CancellationToken);
                    break;
                case "full":
                    scheduleResult.SetResult(null);
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                    break;
                case "disposed":
                    scheduleResult.SetException(new ObjectDisposedException(shard.Id));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected outcome: {outcome}");
            }

            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, shard.DisposeCallCount);
        }
        finally
        {
            scheduleResult.TrySetResult(expectedJob);
            shard.AllowDispose.TrySetResult();
            await (stop ?? observer.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(0, shardManager.CreateShardCallCount);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Equal(active, shard.ConsumeStarted.Task.IsCompleted);
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        AssertSchedulingCacheEmpty(accessor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_DuringConcurrentScheduling_DrainsEveryWriteBeforeDisposal(bool active)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var start = active ? timeProvider.GetUtcNow() : timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("concurrent-writes", start, start.Add(options.ShardDuration));
        var calls = Enumerable.Range(0, 8).Select(i => new
        {
            Request = CreateScheduleRequest(start, $"job-{i}"),
            Started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously),
            Result = new TaskCompletionSource<DurableJob?>(TaskCreationOptions.RunContinuationsAsynchronously)
        }).ToArray();
        var callsByName = calls.ToDictionary(call => call.Request.JobName);
        shard.ScheduleJob = (request, token) =>
        {
            var call = callsByName[request.JobName];
            call.Started.SetResult(token);
            return call.Result.Task;
        };
        var expectedJobs = calls.Select(call => new DurableJob
        {
            Id = call.Request.JobName,
            Name = call.Request.JobName,
            DueTime = call.Request.DueTime,
            TargetGrainId = call.Request.Target,
            ShardId = shard.Id
        }).ToArray();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        accessor.AddWritableShard(start, shard);
        if (active)
        {
            accessor.TryActivateShard(shard);
            await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        var scheduling = calls.Select(call => Task.Run(
            () => manager.ScheduleJobAsync(call.Request, cancellationToken), cancellationToken)).ToArray();
        Task? stop = null;
        try
        {
            var schedulingTokens = await Task.WhenAll(calls.Select(call => call.Started.Task))
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.All(scheduling, task => Assert.False(task.IsCompleted));

            stop = observer.OnStop(cancellationToken);
            Assert.All(schedulingTokens, token => Assert.True(token.IsCancellationRequested));

            for (var i = 0; i < calls.Length; i++)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => manager.ScheduleJobAsync(CreateScheduleRequest(start, $"after-close-{i}"), cancellationToken));
                await manager.QueueTask(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                Assert.False(stop.IsCompleted);
                Assert.False(shard.DisposeStarted.Task.IsCompleted);
                calls[i].Result.SetResult(expectedJobs[i]);
                Assert.Same(expectedJobs[i], await scheduling[i].WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            }

            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, shard.DisposeCallCount);
        }
        finally
        {
            for (var i = 0; i < calls.Length; i++)
            {
                calls[i].Result.TrySetResult(expectedJobs[i]);
            }

            shard.AllowDispose.TrySetResult();
            await (stop ?? observer.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(0, shardManager.CreateShardCallCount);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        AssertSchedulingCacheEmpty(accessor);
    }

    [Fact]
    public async Task Stop_DuringSchedulingAdmission_DrainsCancellationAndReentrantRejection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("admission-canceled", start, start.AddHours(1));
        shard.ScheduleJob = (_, _) => throw new InvalidOperationException("Canceled scheduling reached the shard.");
        var shardManager = new TestJobShardManager();
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var manager = CreateManager(shardManager, timeProvider, CreateOptions(), logger: logger);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        accessor.AddWritableShard(start, shard);
        var request = CreateScheduleRequest(start);
        Task? stop = null;
        Task<DurableJob>? rejected = null;
        var disposeStartedBeforeAdmissionReleased = false;
        logger.OnLog = eventId =>
        {
            if (eventId.Name == "LogSchedulingJob" && stop is null)
            {
                stop = observer.OnStop(cancellationToken);
                rejected = manager.ScheduleJobAsync(request, cancellationToken);
                disposeStartedBeforeAdmissionReleased = shard.DisposeStarted.Task.IsCompleted;
            }
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ScheduleJobAsync(request, cancellationToken));
            Assert.NotNull(stop);
            Assert.NotNull(rejected);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);
            Assert.False(disposeStartedBeforeAdmissionReleased);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            await (stop ?? observer.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(0, shardManager.CreateShardCallCount);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(shard.ConsumeStarted.Task.IsCompleted);
        AssertSchedulingCacheEmpty(accessor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_DuringFailedOrCanceledCreation_DrainsSchedulingWithoutMaskingOutcome(bool canceled)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var creation = new TaskCompletionSource<IJobShard>(TaskCreationOptions.RunContinuationsAsynchronously);
        var createStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shardManager = new TestJobShardManager
        {
            CreateShard = (_, _, _, token) =>
            {
                createStarted.SetResult(token);
                return creation.Task;
            }
        };
        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        var failure = new InvalidOperationException("Creation failed");
        var scheduling = manager.ScheduleJobAsync(CreateScheduleRequest(timeProvider.GetUtcNow()), cancellationToken);
        var creationToken = await createStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var stop = observer.OnStop(cancellationToken);
        try
        {
            Assert.True(creationToken.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            if (canceled)
            {
                creation.SetCanceled(creationToken);
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                Assert.Equal(creationToken, exception.CancellationToken);
            }
            else
            {
                creation.SetException(failure);
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)));
            }
        }
        finally
        {
            creation.TrySetCanceled(creationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shardManager.CreateShardCallCount);
        Assert.Empty(shardManager.UnregisteredShards);
        AssertSchedulingCacheEmpty(accessor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_ClosesSchedulingGateBeforeCallbacksAndRejectsCallsAfterStop(bool existingShard)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var start = timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("shutdown-cache", start, start.Add(options.ShardDuration));
        shard.ScheduleJob = (_, _) => throw new InvalidOperationException("Scheduling reached a shard after admission closed.");
        var shardManager = new TestJobShardManager();
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var manager = CreateManager(shardManager, timeProvider, options, logger: logger);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        accessor.AddWritableShard(start, shard);
        var request = CreateScheduleRequest(existingShard ? start : shard.EndTime);
        Task<DurableJob>? rejected = null;
        logger.OnLog = eventId =>
        {
            if (eventId.Name == "LogStopping")
            {
                rejected = manager.ScheduleJobAsync(request, cancellationToken);
            }
        };

        var stop = observer.OnStop(cancellationToken);
        try
        {
            Assert.NotNull(rejected);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, shard.DisposeCallCount);
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ScheduleJobAsync(request, cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        Assert.Equal(0, shardManager.CreateShardCallCount);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(shard.ConsumeStarted.Task.IsCompleted);
        AssertSchedulingCacheEmpty(accessor);
    }

    [Fact]
    public async Task ScheduleJobAsync_CanceledRequestOwnsLateCreationUntilShutdown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var start = timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("caller-canceled-creation", start, start.Add(options.ShardDuration));
        shard.ScheduleJob = (_, _) => throw new InvalidOperationException("A canceled request must not schedule a job.");
        var creation = new TaskCompletionSource<IJobShard>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shardManager = new TestJobShardManager { CreateShard = (_, _, _, _) => creation.Task };
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        var scheduling = manager.ScheduleJobAsync(CreateScheduleRequest(start), requestCancellation.Token);
        Task? stop = null;
        try
        {
            Assert.Equal(1, shardManager.CreateShardCallCount);
            requestCancellation.Cancel();
            Assert.False(scheduling.IsCompleted);
            creation.SetResult(shard);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduling.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.True(accessor.HasCachedShard(shard.Id));
            Assert.True(accessor.TryGetWritableShard(start, out var writable));
            Assert.Same(shard, writable);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);
            Assert.False(shard.ConsumeStarted.Task.IsCompleted);

            stop = observer.OnStop(cancellationToken);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            creation.TrySetResult(shard);
            shard.AllowDispose.TrySetResult();
            await (stop ?? observer.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shardManager.CreateShardCallCount);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        AssertSchedulingCacheEmpty(accessor);
    }

    [Fact]
    public async Task ScheduleJobAsync_ConcurrentCallsShareCreationAndLeaveNoShutdownWork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var start = timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("shared-creation", start, start.Add(options.ShardDuration));
        var scheduleCount = 0;
        shard.ScheduleJob = (request, _) =>
        {
            Interlocked.Increment(ref scheduleCount);
            return Task.FromResult<DurableJob?>(new()
            {
                Id = request.JobName,
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = shard.Id
            });
        };
        var creation = new TaskCompletionSource<IJobShard>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shardManager = new TestJobShardManager { CreateShard = (_, _, _, _) => creation.Task };
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        var requests = Enumerable.Range(0, 3).Select(i => CreateScheduleRequest(start, $"job-{i}")).ToArray();
        var scheduling = requests.Select(request => manager.ScheduleJobAsync(request, cancellationToken)).ToArray();
        Task? stop = null;
        try
        {
            Assert.Equal(1, shardManager.CreateShardCallCount);
            Assert.All(scheduling, task => Assert.False(task.IsCompleted));
            creation.SetResult(shard);
            var jobs = await Task.WhenAll(scheduling).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(requests.Select(request => request.JobName), jobs.Select(job => job.Name));
            Assert.All(jobs, job => Assert.Equal(shard.Id, job.ShardId));
            Assert.Equal(3, Volatile.Read(ref scheduleCount));
            Assert.Equal(1, shardManager.CreateShardCallCount);
            Assert.Equal(1, accessor.CachedShardCount);
            Assert.True(accessor.TryGetWritableShard(start, out var writable));
            Assert.Same(shard, writable);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);

            stop = observer.OnStop(cancellationToken);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            creation.TrySetResult(shard);
            shard.AllowDispose.TrySetResult();
            await (stop ?? observer.OnStop(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        AssertSchedulingCacheEmpty(accessor);
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenWritableShardIsDisposed_RemovesStaleShardAndRetries()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var dueTime = timeProvider.GetUtcNow().AddMinutes(5);
        var shardKey = new DateTimeOffset(
            (dueTime.UtcTicks / options.ShardDuration.Ticks) * options.ShardDuration.Ticks,
            TimeSpan.Zero);
        var staleShard = new DisposedSchedulingShard("disposed-writable-shard", shardKey, shardKey.Add(options.ShardDuration));
        var replacementShard = new SchedulingShard("replacement-writable-shard", shardKey, shardKey.Add(options.ShardDuration));
        shardManager.CreateShard = (minDueTime, maxDueTime, _, _) =>
        {
            Assert.Equal(shardKey, minDueTime);
            Assert.Equal(shardKey.Add(options.ShardDuration), maxDueTime);
            return Task.FromResult<IJobShard>(replacementShard);
        };

        accessor.AddWritableShard(shardKey, staleShard);

        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "retried-job",
            DueTime = dueTime
        }, CancellationToken.None);

        Assert.Equal("retried-job", job.Name);
        Assert.Equal(replacementShard.Id, job.ShardId);
        Assert.Equal(1, shardManager.CreateShardCallCount);
        Assert.True(accessor.TryGetWritableShard(shardKey, out var currentShard));
        Assert.Same(replacementShard, currentShard);
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenExpiryWaitsBehindScheduling_CompletesShardAfterJobIsAccepted()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var shard = new GateableSchedulingShard("expiring-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);

        var scheduleTask = manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "late-job",
            DueTime = shardKey
        }, CancellationToken.None);

        await shard.ScheduleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var cycleTask = accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(cycleTask.IsCompleted);

        shard.AllowScheduleToFinish.SetResult();

        var job = await scheduleTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cycleTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("late-job", job.Name);
        Assert.Equal(1, shard.MarkAsCompleteCallCount);
        Assert.True(shard.IsAddingCompleted);
        Assert.False(accessor.HasWritableShard(shardKey));
    }

    [Fact]
    public async Task Stop_ClosesCancellationAdmissionBeforeCallbacksAndAfterStop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var shardManager = new TestJobShardManager
        {
            GetShardOwner = (_, _) => throw new InvalidOperationException("Rejected cancellation reached ownership lookup.")
        };
        var manager = CreateManager(shardManager, timeProvider, CreateOptions(), logger: logger);
        var job = new DurableJob
        {
            Id = "job-1",
            Name = "job",
            DueTime = timeProvider.GetUtcNow(),
            TargetGrainId = GrainId.Create("test", "job"),
            ShardId = "closed-shard"
        };
        Task<bool>? rejected = null;
        logger.OnLog = eventId =>
        {
            if (eventId.Name == "LogStopping")
            {
                rejected = manager.CancelAsync(job, cancellationToken);
            }
        };

        await CreateLifecycleObserver(manager).OnStop(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.NotNull(rejected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.CancelAsync(job, cancellationToken));
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(false, "failure")]
    [InlineData(false, "cancellation")]
    [InlineData(true, "success")]
    [InlineData(true, "failure")]
    [InlineData(true, "cancellation")]
    public async Task Stop_DuringCancellationRemoval_DrainsOutcomeBeforeDisposal(bool active, string outcome)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = active ? timeProvider.GetUtcNow() : timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("pending-cancellation", start, start.AddHours(1));
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<DurableJobMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        shard.RemoveJob = (_, token) =>
        {
            started.SetResult(token);
            return result.Task;
        };
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        accessor.AddWritableShard(start, shard);
        if (active)
        {
            accessor.TryActivateShard(shard);
            await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        var job = new DurableJob
        {
            Id = "job-1",
            Name = "job",
            DueTime = start,
            TargetGrainId = GrainId.Create("test", "job"),
            ShardId = shard.Id
        };
        var cancellation = manager.CancelAsync(job, cancellationToken);
        var operationToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var stop = observer.OnStop(cancellationToken);
        try
        {
            Assert.True(operationToken.IsCancellationRequested);
            await manager.QueueTask(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);
            Assert.Empty(shardManager.UnregisteredShards);

            switch (outcome)
            {
                case "success":
                    result.SetResult(DurableJobMutationResult.Applied);
                    Assert.True(await cancellation.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                    break;
                case "failure":
                    var failure = new InvalidOperationException("Cancellation persistence failed.");
                    result.SetException(failure);
                    Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                        () => cancellation.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)));
                    break;
                case "cancellation":
                    result.SetCanceled(operationToken);
                    var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => cancellation.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                    Assert.Equal(operationToken, exception.CancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected outcome: {outcome}");
            }

            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            result.TrySetResult(DurableJobMutationResult.Applied);
            shard.AllowDispose.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shard.DisposeCallCount);
        AssertSchedulingCacheEmpty(accessor);
    }

    [Theory]
    [InlineData("ownership")]
    [InlineData("owner")]
    [InlineData("remote")]
    public async Task Stop_DuringCancellationRouting_CancelsAndDrainsRequest(string phase)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = timeProvider.GetUtcNow().AddHours(1);
        var shard = new BlockingQueueShard("routing-cancellation", start, start.AddHours(1));
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shardManager = new TestJobShardManager();
        var remoteOwner = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var remote = Substitute.For<ILocalDurableJobManagerSystemTarget>();
        grainFactory.GetSystemTarget<ILocalDurableJobManagerSystemTarget>(
            LocalDurableJobManager.JobManagerGrainType, remoteOwner).Returns(remote);
        if (phase == "ownership")
        {
            shardManager.IsShardOwned = (_, token) => new(BlockAsync(token, true));
        }
        else if (phase == "owner")
        {
            shardManager.GetShardOwner = (_, token) => new(BlockAsync<SiloAddress?>(token, remoteOwner));
        }
        else
        {
            shardManager.ShardOwner = remoteOwner;
            remote.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>())
                .Returns(call => BlockAsync(call.ArgAt<CancellationToken>(1), true));
        }

        var manager = CreateManager(shardManager, timeProvider, CreateOptions(), grainFactory);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var observer = CreateLifecycleObserver(manager);
        accessor.AddWritableShard(start, shard);
        var job = new DurableJob
        {
            Id = "job-1",
            Name = "job",
            DueTime = start,
            TargetGrainId = GrainId.Create("test", "job"),
            ShardId = phase == "ownership" ? shard.Id : "remote-shard"
        };
        var cancellation = manager.CancelAsync(job, cancellationToken);
        var operationToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var stop = observer.OnStop(cancellationToken);
        try
        {
            Assert.True(operationToken.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            Assert.False(shard.DisposeStarted.Task.IsCompleted);
            Assert.Empty(shardManager.UnregisteredShards);

            release.SetResult();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancellation.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.Equal(operationToken, exception.CancellationToken);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            shard.AllowDispose.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(1, shard.DisposeCallCount);
        AssertSchedulingCacheEmpty(accessor);

        async Task<T> BlockAsync<T>(CancellationToken token, T value)
        {
            started.SetResult(token);
            await release.Task;
            token.ThrowIfCancellationRequested();
            return value;
        }
    }

    [Fact]
    public async Task CancelAsync_WhenRemoveSucceeds_ReportsCancellationRequestAccepted()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var manager = CreateManager(new TestJobShardManager(), timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var shard = CreateSubstituteShard("cancellation-shard", shardKey, shardKey.Add(options.ShardDuration));
        shard.RemoveJobAsync("job-1", Arg.Any<CancellationToken>()).Returns(DurableJobMutationResult.Applied);
        accessor.AddWritableShard(shardKey, shard);
        var job = new DurableJob
        {
            Id = "job-1",
            Name = "job",
            DueTime = shardKey,
            TargetGrainId = GrainId.Create("test", "job"),
            ShardId = shard.Id
        };

        var cancellationRequested = await manager.CancelAsync(job, CancellationToken.None);

        Assert.True(cancellationRequested);
        await shard.Received(1).RemoveJobAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAsync_AfterProviderCutoverUsesOriginalHandleAndJournalWhileNewSchedulesUseWriteProvider(bool routeFromUncachedSilo)
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var a = new VolatileJournalStorageProvider();
        var b = new VolatileJournalStorageProvider();
        await using var servicesA = CreateJournaledServices(a, time);
        await using var servicesB = CreateJournaledServices(b, time);
        var bindingA = new DurableJobsJournalProvider("A", a, a, servicesA.GetRequiredService<IJournaledStateManagerFactory>());
        var bindingB = new DurableJobsJournalProvider("B", b, b, servicesB.GetRequiredService<IJournaledStateManagerFactory>());
        var owner = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        var caller = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membership = new TestClusterMembershipService();
        membership.SetSiloStatus(owner, SiloStatus.Active);
        membership.SetSiloStatus(caller, SiloStatus.Active);
        var options = CreateOptions();
        options.ActiveProviderName = "B";
        options.DrainingProviderNames.Add("A");
        var managerOptions = servicesB.GetRequiredService<IOptions<JournaledStateManagerOptions>>();
        var original = new JournaledJobShardManager(new TestLocalSiloDetails(owner),
            new DurableJobsJournalProviders(bindingA, bindingB), membership, servicesA, Options.Create(options), managerOptions);
        var future = time.GetUtcNow().AddYears(10);
        await using var shard = await original.CreateShardAsync(future, future.Add(options.ShardDuration), new Dictionary<string, string>(), token);
        var handle = await shard.TryScheduleJobAsync(CreateScheduleRequest(future, "before-cutover"), token);
        Assert.NotNull(handle);
        await original.UnregisterShardAsync(shard, token);

        var cutover = new JournaledJobShardManager(new TestLocalSiloDetails(owner),
            new DurableJobsJournalProviders(bindingB, bindingA), membership, servicesB, Options.Create(options), managerOptions);
        var recovered = Assert.Single(await cutover.AssignJobShardsAsync(future, 1, token));
        Assert.True(recovered.IsAddingCompleted);
        Assert.Equal(handle.ShardId, recovered.Id);
        var receiver = CreateManager(cutover, time, options, siloAddress: owner);
        var receiverAccessor = new LocalDurableJobManager.TestAccessor(receiver);
        receiverAccessor.AddWritableShard(future, recovered);
        var remote = Substitute.For<ILocalDurableJobManagerSystemTarget>();
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<ILocalDurableJobManagerSystemTarget>(LocalDurableJobManager.JobManagerGrainType, owner)
            .Returns(remote);
        remote.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>())
            .Returns(call => receiver.CancelAsync(call.ArgAt<DurableJob>(0), call.ArgAt<CancellationToken>(1)));
        var lookup = new JournaledJobShardManager(new TestLocalSiloDetails(caller),
            new DurableJobsJournalProviders(bindingB, bindingA), membership, servicesB, Options.Create(options), managerOptions);
        var routingManager = CreateManager(lookup, time, options, grainFactory);
        try
        {
            // Scheduling encounters the recovered closed shard but must create a writable shard in B.
            var newHandle = await receiver.ScheduleJobAsync(CreateScheduleRequest(future, "after-cutover"), token);
            Assert.NotEqual(handle.ShardId, newHandle.ShardId);
            var newId = JobShardId.Parse(newHandle.ShardId).ToJournalId();
            var oldId = JobShardId.Parse(handle.ShardId).ToJournalId();
            Assert.NotNull(await b.CreateStorage(newId).GetMetadataAsync(token));
            Assert.Null(await a.CreateStorage(newId).GetMetadataAsync(token));
            Assert.Null(await b.CreateStorage(oldId).GetMetadataAsync(token));

            Assert.True(await (routeFromUncachedSilo ? routingManager : receiver).CancelAsync(handle, token));
            Assert.Equal(0, await recovered.GetJobCountAsync());
            Assert.False(await receiver.CancelAsync(handle, token));
            if (routeFromUncachedSilo)
            {
                await remote.Received(1).CancelAsync(handle, Arg.Any<CancellationToken>());
            }
            else
            {
                await remote.DidNotReceive().CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>());
            }

            await CreateLifecycleObserver(receiver).OnStop(token);
            Assert.Null(await a.CreateStorage(oldId).GetMetadataAsync(token));
            Assert.NotNull(await b.CreateStorage(newId).GetMetadataAsync(token));
        }
        finally
        {
            await CreateLifecycleObserver(receiver).OnStop(token);
            await CreateLifecycleObserver(routingManager).OnStop(token);
            await recovered.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelAsync_NullJob_Throws()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var manager = CreateManager(new TestJobShardManager(), timeProvider, CreateOptions());

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => manager.CancelAsync(null!, CancellationToken.None));

        Assert.Equal("job", exception.ParamName);
    }

    [Fact]
    public async Task CancelAsync_WhenCachedShardOwnershipMoved_RoutesToCurrentOwner()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var owner = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        var shardManager = new TestJobShardManager
        {
            IsLocallyOwned = false,
            ShardOwner = owner
        };
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var remoteManager = Substitute.For<ILocalDurableJobManagerSystemTarget>();
        remoteManager.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(true);
        grainFactory.GetSystemTarget<ILocalDurableJobManagerSystemTarget>(
                LocalDurableJobManager.JobManagerGrainType,
                owner)
            .Returns(remoteManager);
        var manager = CreateManager(shardManager, timeProvider, options, grainFactory);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var shard = CreateSubstituteShard("cancellation-shard", shardKey, shardKey.Add(options.ShardDuration));
        accessor.AddWritableShard(shardKey, shard);
        var job = new DurableJob
        {
            Id = "job-1",
            Name = "job",
            DueTime = shardKey,
            TargetGrainId = GrainId.Create("test", "job"),
            ShardId = shard.Id
        };

        var cancellationRequested = await manager.CancelAsync(job, CancellationToken.None);

        Assert.True(cancellationRequested);
        Assert.False(accessor.HasCachedShard(shard.Id));
        await shard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await remoteManager.Received(1).CancelAsync(job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAsync_WhenOwnershipReturnsLocally_UsesReplacementCachedShard()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var localOwner = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var shardManager = new TestJobShardManager { IsLocallyOwned = false };
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var staleShard = CreateSubstituteShard("cancellation-shard", shardKey, shardKey.Add(options.ShardDuration));
        var replacementShard = CreateSubstituteShard("cancellation-shard", shardKey, shardKey.Add(options.ShardDuration));
        replacementShard.RemoveJobAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(DurableJobMutationResult.Applied);
        accessor.AddWritableShard(shardKey, staleShard);
        shardManager.GetShardOwner = (_, _) =>
        {
            accessor.AddWritableShard(shardKey, replacementShard);
            shardManager.IsLocallyOwned = true;
            return ValueTask.FromResult<SiloAddress?>(localOwner);
        };
        var job = new DurableJob
        {
            Id = "job-1",
            Name = "job",
            DueTime = shardKey,
            TargetGrainId = GrainId.Create("test", "job"),
            ShardId = staleShard.Id
        };

        var cancellationRequested = await manager.CancelAsync(job, CancellationToken.None);

        Assert.True(cancellationRequested);
        Assert.True(accessor.HasCachedShard(replacementShard.Id));
        await staleShard.DidNotReceive().RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await replacementShard.Received(1).RemoveJobAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenShardStripingEnabled_DistributesJobsAcrossWritableShards()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardStripeCount = 4;
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var dueTime = timeProvider.GetUtcNow().AddMinutes(5);
        var shardKey = new DateTimeOffset(
            (dueTime.UtcTicks / options.ShardDuration.Ticks) * options.ShardDuration.Ticks,
            TimeSpan.Zero);
        var targetsByStripe = new Dictionary<int, GrainId>();
        for (var i = 0; i < 10_000 && targetsByStripe.Count < options.ShardStripeCount; i++)
        {
            var target = GrainId.Create("test", $"target-{i}");
            var request = new ScheduleJobRequest
            {
                Target = target,
                JobName = "striped-job",
                DueTime = dueTime
            };
            targetsByStripe.TryAdd(accessor.GetWritableShardStripe(request), target);
        }

        Assert.Equal(options.ShardStripeCount, targetsByStripe.Count);

        shardManager.CreateShard = (minDueTime, maxDueTime, metadata, _) =>
        {
            Assert.Equal(shardKey, minDueTime);
            Assert.Equal(shardKey.Add(options.ShardDuration), maxDueTime);
            Assert.True(metadata.TryGetValue("stripe", out var stripe));
            return Task.FromResult<IJobShard>(new SchedulingShard($"stripe-shard-{stripe}", minDueTime, maxDueTime));
        };

        var initialJobs = await Task.WhenAll(targetsByStripe
            .OrderBy(static entry => entry.Key)
            .Select(entry => manager.ScheduleJobAsync(new()
            {
                Target = entry.Value,
                JobName = "striped-job",
                DueTime = dueTime
            }, CancellationToken.None)));

        Assert.Equal(options.ShardStripeCount, shardManager.CreateShardCallCount);
        Assert.Equal(options.ShardStripeCount, accessor.WritableShardCount);
        Assert.Equal(options.ShardStripeCount, initialJobs.Select(static job => job.ShardId).Distinct().Count());

        var secondRoundJobs = await Task.WhenAll(targetsByStripe
            .OrderBy(static entry => entry.Key)
            .Select(entry => manager.ScheduleJobAsync(new()
            {
                Target = entry.Value,
                JobName = "striped-job",
                DueTime = dueTime
            }, CancellationToken.None)));

        Assert.Equal(options.ShardStripeCount, shardManager.CreateShardCallCount);
        Assert.Equal(
            initialJobs.Select(static job => job.ShardId).OrderBy(static id => id).ToArray(),
            secondRoundJobs.Select(static job => job.ShardId).OrderBy(static id => id).ToArray());
    }

    [Fact]
    public async Task ExpiredJournaledShard_DrainsUnregistersAndDeletesStorage()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardDuration = TimeSpan.FromSeconds(1);
        var storageProvider = new VolatileJournalStorageProvider();
        await using var services = CreateJournaledServices(storageProvider, timeProvider);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5010), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        var membership = new TestClusterMembershipService();
        membership.SetSiloStatus(siloAddress, SiloStatus.Active);
        var optionsWrapper = Options.Create(options);
        var journaledShardManager = new JournaledJobShardManager(
            localSiloDetails,
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            optionsWrapper,
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
        var (grainFactory, handledJob) = CreateCompletingGrainFactory();
        var overloadDetector = Substitute.For<IOverloadDetector>();
        overloadDetector.IsOverloaded.Returns(false);
        var shardExecutor = new ShardExecutor(
            grainFactory,
            optionsWrapper,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider);
        var manager = new LocalDurableJobManager(
            journaledShardManager,
            shardExecutor,
            grainFactory,
            membership,
            overloadDetector,
            timeProvider,
            optionsWrapper,
            CreateSystemTargetShared(localSiloDetails),
            NullLogger<LocalDurableJobManager>.Instance);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "journaled-job",
            DueTime = timeProvider.GetUtcNow()
        }, CancellationToken.None);

        Assert.True(accessor.TryGetRunningShardTask(job.ShardId, out var runTask));

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        var jobContext = await handledJob.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(job.Id, jobContext.Job.Id);
        await AdvanceUntilCompletedAsync(
            timeProvider,
            runTask!,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(await storageProvider
            .CreateStorage(JobShardId.Parse(job.ShardId).ToJournalId())
            .GetMetadataAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProviderCutover_DiscoveredOldJobExecutesAndDeletesAWhileNewFutureJobRemainsInB()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var a = new VolatileJournalStorageProvider();
        var b = new VolatileJournalStorageProvider();
        await using var servicesA = CreateJournaledServices(a, time);
        await using var servicesB = CreateJournaledServices(b, time);
        var bindingA = new DurableJobsJournalProvider("A", a, a, servicesA.GetRequiredService<IJournaledStateManagerFactory>());
        var bindingB = new DurableJobsJournalProvider("B", b, b, servicesB.GetRequiredService<IJournaledStateManagerFactory>());
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var details = new TestLocalSiloDetails(silo);
        var membership = new TestClusterMembershipService();
        membership.SetSiloStatus(silo, SiloStatus.Active);
        var options = CreateOptions();
        var original = new JournaledJobShardManager(details, new DurableJobsJournalProviders(bindingA, bindingB),
            membership, servicesA, Options.Create(options), servicesA.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
        await using var shard = await original.CreateShardAsync(time.GetUtcNow(), time.GetUtcNow().AddMinutes(1),
            new Dictionary<string, string>(), token);
        var oldJob = await shard.TryScheduleJobAsync(CreateScheduleRequest(time.GetUtcNow(), "execute-from-A"), token);
        Assert.NotNull(oldJob);
        await original.UnregisterShardAsync(shard, token);
        var cutover = new JournaledJobShardManager(details, new DurableJobsJournalProviders(bindingB, bindingA),
            membership, servicesB, Options.Create(options), servicesB.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
        var recovered = Assert.Single(await cutover.AssignJobShardsAsync(time.GetUtcNow(), 1, token));
        var handled = new TaskCompletionSource<IJobRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<DurableJobRunResult>(HandleAsync(call.ArgAt<IJobRunContext>(0))));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        var manager = CreateManager(cutover, time, options, grainFactory);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        try
        {
            var futureJob = await manager.ScheduleJobAsync(CreateScheduleRequest(time.GetUtcNow().AddYears(1), "remain-in-B"), token);
            accessor.TryActivateShard(recovered);
            var context = await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(oldJob.Id, context.Job.Id);
            Assert.Equal(oldJob.ShardId, context.Job.ShardId);
            Assert.Equal("execute-from-A", context.Job.Name);
            Assert.Equal(1, context.DequeueCount);
            Assert.True(accessor.TryGetRunningShardTask(recovered.Id, out var running));
            complete.SetResult();
            await running!.WaitAsync(TimeSpan.FromSeconds(5), token);

            var oldId = JobShardId.Parse(oldJob.ShardId).ToJournalId();
            var newId = JobShardId.Parse(futureJob.ShardId).ToJournalId();
            Assert.Null(await a.CreateStorage(oldId).GetMetadataAsync(token));
            Assert.Null(await b.CreateStorage(oldId).GetMetadataAsync(token));
            Assert.Null(await a.CreateStorage(newId).GetMetadataAsync(token));
            Assert.NotNull(await b.CreateStorage(newId).GetMetadataAsync(token));
            await extension.Received(1).HandleDurableJobAsync(
                Arg.Is<IJobRunContext>(run => run.Job.Id == oldJob.Id), Arg.Any<CancellationToken>());
            await extension.DidNotReceive().HandleDurableJobAsync(
                Arg.Is<IJobRunContext>(run => run.Job.Id == futureJob.Id), Arg.Any<CancellationToken>());
        }
        finally
        {
            complete.TrySetResult();
            await CreateLifecycleObserver(manager).OnStop(token);
            await recovered.DisposeAsync();
        }

        async Task<DurableJobRunResult> HandleAsync(IJobRunContext context)
        {
            handled.TrySetResult(context);
            await complete.Task.WaitAsync(token);
            return DurableJobRunResult.Completed;
        }
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenJobIsDueNow_DispatchesWithoutAdvancingTime()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardDuration = TimeSpan.FromSeconds(1);
        var storageProvider = new VolatileJournalStorageProvider();
        await using var services = CreateJournaledServices(storageProvider, timeProvider);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5010), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        var membership = new TestClusterMembershipService();
        membership.SetSiloStatus(siloAddress, SiloStatus.Active);
        var optionsWrapper = Options.Create(options);
        var journaledShardManager = new JournaledJobShardManager(
            localSiloDetails,
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            optionsWrapper,
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
        var (grainFactory, handledJob, getHandleCount) = CreateCountingGrainFactory(timeProvider);
        var overloadDetector = Substitute.For<IOverloadDetector>();
        overloadDetector.IsOverloaded.Returns(false);
        var shardExecutor = new ShardExecutor(
            grainFactory,
            optionsWrapper,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider);
        var manager = new LocalDurableJobManager(
            journaledShardManager,
            shardExecutor,
            grainFactory,
            membership,
            overloadDetector,
            timeProvider,
            optionsWrapper,
            CreateSystemTargetShared(localSiloDetails),
            NullLogger<LocalDurableJobManager>.Instance);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);

        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "due-now-job",
            DueTime = timeProvider.GetUtcNow()
        }, CancellationToken.None);

        Assert.True(accessor.TryGetRunningShardTask(job.ShardId, out var runTask));
        var jobContext = await handledJob.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(job.Id, jobContext.Job.Id);
        Assert.Equal(1, getHandleCount());

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);
        await AdvanceUntilCompletedAsync(
            timeProvider,
            runTask!,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, getHandleCount());
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenJobIsDueNow_WaitsForShardConsumer()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var (grainFactory, handledJob, _) = CreateCountingGrainFactory(timeProvider);
        var manager = CreateManager(shardManager, timeProvider, options, grainFactory);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var shard = new ControlledConsumingShard("controlled-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);
        accessor.TryActivateShard(shard);
        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "due-now-job",
            DueTime = shardKey
        }, CancellationToken.None);

        var unexpectedDispatch = await Task.WhenAny(
            handledJob.Task,
            Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        Assert.NotSame(handledJob.Task, unexpectedDispatch);

        shard.AllowConsume.SetResult();

        var jobContext = await handledJob.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(job.Id, jobContext.Job.Id);
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenJobIsDueInFuture_DoesNotDispatchImmediately()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardDuration = TimeSpan.FromSeconds(1);
        var storageProvider = new VolatileJournalStorageProvider();
        await using var services = CreateJournaledServices(storageProvider, timeProvider);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5010), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        var membership = new TestClusterMembershipService();
        membership.SetSiloStatus(siloAddress, SiloStatus.Active);
        var optionsWrapper = Options.Create(options);
        var journaledShardManager = new JournaledJobShardManager(
            localSiloDetails,
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            optionsWrapper,
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
        var (grainFactory, handledJob, getHandleCount) = CreateCountingGrainFactory(timeProvider);
        var overloadDetector = Substitute.For<IOverloadDetector>();
        overloadDetector.IsOverloaded.Returns(false);
        var shardExecutor = new ShardExecutor(
            grainFactory,
            optionsWrapper,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider);
        var manager = new LocalDurableJobManager(
            journaledShardManager,
            shardExecutor,
            grainFactory,
            membership,
            overloadDetector,
            timeProvider,
            optionsWrapper,
            CreateSystemTargetShared(localSiloDetails),
            NullLogger<LocalDurableJobManager>.Instance);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);

        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "future-job",
            DueTime = timeProvider.GetUtcNow().AddSeconds(5)
        }, CancellationToken.None);

        Assert.False(handledJob.Task.IsCompleted);
        Assert.Equal(0, getHandleCount());
        Assert.False(accessor.TryGetRunningShardTask(job.ShardId, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_WithInactiveJournaledShard_ReleasesOwnershipOrDeletesEmptyShard(bool hasJob)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var storageProvider = new VolatileJournalStorageProvider();
        await using var services = CreateJournaledServices(storageProvider, timeProvider);
        var membership = new TestClusterMembershipService();
        var local = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var other = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        membership.SetSiloStatus(local, SiloStatus.Active);
        membership.SetSiloStatus(other, SiloStatus.Active);
        var shardManager = CreateShardManager(local);
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var start = timeProvider.GetUtcNow().AddHours(1);
        var shard = Assert.IsType<JournaledJobShard>(await shardManager.CreateShardAsync(
            start, start.Add(options.ShardDuration), new Dictionary<string, string>(), cancellationToken));
        accessor.AddWritableShard(start, shard);
        DurableJob? job = null;
        if (hasJob)
        {
            job = await manager.ScheduleJobAsync(CreateScheduleRequest(start), cancellationToken);
        }

        Assert.True(await shardManager.IsShardOwnedByLocalSiloAsync(shard.Id, cancellationToken));
        Assert.Equal(local, await shardManager.GetShardOwnerAsync(shard.Id, cancellationToken));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));

        await CreateLifecycleObserver(manager).OnStop(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        AssertSchedulingCacheEmpty(accessor);
        Assert.False(await shardManager.IsShardOwnedByLocalSiloAsync(shard.Id, cancellationToken));
        Assert.Null(await shardManager.GetShardOwnerAsync(shard.Id, cancellationToken));
        var otherManager = CreateShardManager(other);
        var claimed = await otherManager.AssignJobShardsAsync(start, int.MaxValue, cancellationToken);
        if (hasJob)
        {
            var replacement = Assert.Single(claimed);
            try
            {
                Assert.NotNull(job);
                Assert.Equal(shard.Id, replacement.Id);
                Assert.True(replacement.IsAddingCompleted);
                Assert.Equal(1, await replacement.GetJobCountAsync());
                Assert.Equal(DurableJobMutationResult.Applied, await replacement.RemoveJobAsync(job.Id, cancellationToken));
            }
            finally
            {
                await otherManager.UnregisterShardAsync(replacement, cancellationToken);
            }
        }
        else
        {
            Assert.Empty(claimed);
        }

        Assert.Null(await storageProvider.CreateStorage(shard.StorageId).GetMetadataAsync(cancellationToken));

        JournaledJobShardManager CreateShardManager(SiloAddress silo) => new(
            new TestLocalSiloDetails(silo),
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            Options.Create(options),
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_WhenInactiveUnregistrationFails_DisposesEveryShard(bool canceled)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var shutdownToken = canceled ? new CancellationToken(canceled: true) : cancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = timeProvider.GetUtcNow().AddHours(1);
        var first = new BlockingQueueShard("first-inactive", start, start.AddHours(1));
        var second = new BlockingQueueShard("second-inactive", start.AddHours(1), start.AddHours(2));
        first.AllowDispose.SetResult();
        second.AllowDispose.SetResult();
        var failure = new InvalidOperationException("Unregistration failed.");
        var tokens = new List<CancellationToken>();
        var shardManager = new TestJobShardManager
        {
            UnregisterShard = (_, token) =>
            {
                tokens.Add(token);
                return canceled ? Task.FromCanceled(token) : Task.FromException(failure);
            }
        };
        var logger = new RecordingLogger<LocalDurableJobManager>();
        var manager = CreateManager(shardManager, timeProvider, CreateOptions(), logger: logger);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        accessor.AddWritableShard(first.StartTime, first);
        accessor.AddWritableShard(second.StartTime, second);

        await CreateLifecycleObserver(manager).OnStop(shutdownToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(2, shardManager.UnregisteredShards.Count);
        Assert.Contains(first, shardManager.UnregisteredShards);
        Assert.Contains(second, shardManager.UnregisteredShards);
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.Equal(shutdownToken, token));
        Assert.Equal(1, first.DisposeCallCount);
        Assert.Equal(1, second.DisposeCallCount);
        var errors = logger.Entries.Where(entry => entry.Level == LogLevel.Error).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.All(errors, entry =>
        {
            if (canceled)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(entry.Exception);
            }
            else
            {
                Assert.Same(failure, entry.Exception);
            }
        });
        AssertSchedulingCacheEmpty(accessor);
    }

    private static DurableJobsOptions CreateOptions() => new()
    {
        ShardDuration = TimeSpan.FromMinutes(1),
        ShardActivationBufferPeriod = TimeSpan.Zero,
        ShardClaimRampUpDuration = TimeSpan.Zero,
        ConcurrencySlowStartEnabled = false,
        MaxConcurrentJobsPerSilo = 10
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ScheduleJobAsync_InvalidJobName_Throws(string? jobName)
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var manager = CreateManager(new TestJobShardManager(), timeProvider, CreateOptions());
        var request = new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "target"),
            JobName = jobName!,
            DueTime = timeProvider.GetUtcNow()
        };

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => manager.ScheduleJobAsync(request, CancellationToken.None));

        Assert.Equal("JobName", exception.ParamName);
        await CreateLifecycleObserver(manager).OnStop(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static ScheduleJobRequest CreateScheduleRequest(DateTimeOffset dueTime, string jobName = "scheduled-job") => new()
    {
        Target = GrainId.Create("test", "target"),
        JobName = jobName,
        DueTime = dueTime
    };

    private static ILifecycleObserver CreateLifecycleObserver(LocalDurableJobManager manager)
    {
        var lifecycle = Substitute.For<ISiloLifecycle>();
        ILifecycleObserver? observer = null;
        lifecycle.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>())
            .Returns(call =>
            {
                observer = call.ArgAt<ILifecycleObserver>(2);
                return Substitute.For<IDisposable>();
            });
        manager.Participate(lifecycle);
        Assert.NotNull(observer);
        return observer;
    }

    private static void AssertSchedulingCacheEmpty(LocalDurableJobManager.TestAccessor accessor)
    {
        Assert.Equal(0, accessor.CachedShardCount);
        Assert.Equal(0, accessor.WritableShardCount);
        Assert.Equal(0, accessor.WritableShardKeyCount);
    }

    private static LocalDurableJobManager CreateManager(
        JobShardManager shardManager,
        FakeTimeProvider timeProvider,
        DurableJobsOptions options,
        IInternalGrainFactory? grainFactory = null,
        ILogger<LocalDurableJobManager>? logger = null,
        SiloAddress? siloAddress = null)
    {
        siloAddress ??= SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        grainFactory ??= Substitute.For<IInternalGrainFactory>();
        var overloadDetector = Substitute.For<IOverloadDetector>();
        overloadDetector.IsOverloaded.Returns(false);
        var optionsWrapper = Options.Create(options);
        var shardExecutor = new ShardExecutor(
            grainFactory,
            optionsWrapper,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider);

        return new LocalDurableJobManager(
            shardManager,
            shardExecutor,
            grainFactory,
            new TestClusterMembershipService(),
            overloadDetector,
            timeProvider,
            optionsWrapper,
            CreateSystemTargetShared(localSiloDetails),
            logger ?? NullLogger<LocalDurableJobManager>.Instance);
    }

    private static SystemTargetShared CreateSystemTargetShared(ILocalSiloDetails localSiloDetails) => new(
        runtimeClient: null!,
        localSiloDetails,
        NullLoggerFactory.Instance,
        Options.Create(new SchedulingOptions()),
        grainReferenceActivator: null!,
        timerRegistry: null!,
        activations: new ActivationDirectory(CreateCatalogInstruments()),
        schedulerInstruments: CreateSchedulerInstruments(),
        grainInstruments: CreateGrainInstruments(),
        messagingInstruments: CreateMessagingInstruments(),
        messagingProcessingInstruments: CreateMessagingProcessingInstruments());

    private static SchedulerInstruments CreateSchedulerInstruments()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<SchedulerInstruments>();
        return services.BuildServiceProvider().GetRequiredService<SchedulerInstruments>();
    }

    private static CatalogInstruments CreateCatalogInstruments()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<CatalogInstruments>();
        return services.BuildServiceProvider().GetRequiredService<CatalogInstruments>();
    }

    private static GrainInstruments CreateGrainInstruments()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<GrainInstruments>();
        return services.BuildServiceProvider().GetRequiredService<GrainInstruments>();
    }

    private static MessagingInstruments CreateMessagingInstruments()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<MessagingInstruments>();
        return services.BuildServiceProvider().GetRequiredService<MessagingInstruments>();
    }

    private static MessagingProcessingInstruments CreateMessagingProcessingInstruments()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<MessagingProcessingInstruments>();
        return services.BuildServiceProvider().GetRequiredService<MessagingProcessingInstruments>();
    }

    private static IJobShard CreateSubstituteShard(string id, DateTimeOffset start, DateTimeOffset end)
    {
        var shard = Substitute.For<IJobShard>();
        shard.Id.Returns(id);
        shard.StartTime.Returns(start);
        shard.EndTime.Returns(end);
        shard.MarkAsCompleteAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return shard;
    }

    private static ServiceProvider CreateJournaledServices(VolatileJournalStorageProvider storageProvider, TimeProvider timeProvider)
    {
        var builder = new TestSiloBuilder();
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        builder.Services.AddLogging();
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        builder.Services.AddSingleton<IJournalStorageProvider>(storageProvider);
        builder.Services.AddSingleton<IJournalStorageCatalog>(storageProvider);
        return builder.Services.BuildServiceProvider();
    }

    private static (IInternalGrainFactory GrainFactory, TaskCompletionSource<IJobRunContext> HandledJob) CreateCompletingGrainFactory()
    {
        var handledJob = new TaskCompletionSource<IJobRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                handledJob.TrySetResult(callInfo.ArgAt<IJobRunContext>(0));
                return DurableJobRunResult.Completed;
            });
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        return (grainFactory, handledJob);
    }

    private static (IInternalGrainFactory GrainFactory, TaskCompletionSource<IJobRunContext> HandledJob, Func<int> GetHandleCount) CreateCountingGrainFactory(TimeProvider timeProvider)
    {
        var handledJob = new TaskCompletionSource<IJobRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleCount = 0;
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Interlocked.Increment(ref handleCount);
                handledJob.TrySetResult(callInfo.ArgAt<IJobRunContext>(0));
                return Task.CompletedTask;
            });

        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(handler);
        grainContext.GrainId.Returns(GrainId.Create("test", "target"));
        var shared = new DurableJobReceiverExtensionShared(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions()),
            Options.Create(new SiloMessagingOptions()),
            timeProvider);
        var extension = new DurableJobReceiverExtension(grainContext, shared);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        return (grainFactory, handledJob, () => Volatile.Read(ref handleCount));
    }

    private static async Task AdvanceUntilCompletedAsync(
        FakeTimeProvider timeProvider,
        Task task,
        TimeSpan advanceBy,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            timeProvider.Advance(advanceBy);
        }
    }

    private sealed class BlockingQueueShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        private readonly InMemoryJobQueue _queue = new();

        public TaskCompletionSource ConsumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<ScheduleJobRequest, CancellationToken, Task<DurableJob?>>? ScheduleJob { get; set; }

        public Func<string, CancellationToken, Task<DurableJobMutationResult>>? RemoveJob { get; set; }

        public int DisposeCallCount;

        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => false;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync()
        {
            ConsumeStarted.TrySetResult();
            return _queue;
        }

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task<DurableJobMutationResult> TryStartAttemptAsync(IJobRunContext jobContext, CancellationToken cancellationToken) =>
            Task.FromResult(DurableJobMutationResult.Applied);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken) =>
            RemoveJob is { } remove ? remove(jobId, cancellationToken) : Task.FromResult(DurableJobMutationResult.JobNotFound);

        public Task<DurableJobMutationResult> RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJobMutationResult> RescheduleJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) =>
            ScheduleJob is { } schedule ? schedule(request, cancellationToken) : Task.FromResult<DurableJob?>(null);

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await AllowDispose.Task;
            Interlocked.Increment(ref DisposeCallCount);
        }
    }

    private sealed class FaultingOnCancellationShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        public TaskCompletionSource ConsumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCallCount;

        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => false;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task<DurableJobMutationResult> TryStartAttemptAsync(IJobRunContext jobContext, CancellationToken cancellationToken) =>
            Task.FromResult(DurableJobMutationResult.Applied);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.JobNotFound);

        public Task<DurableJobMutationResult> RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJobMutationResult> RescheduleJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) => Task.FromResult<DurableJob?>(null);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCallCount);
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<IJobRunContext> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ConsumeStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Unexpected shard failure");
            }

            yield break;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, EventId EventId, Exception? Exception)> Entries { get; } = new();

        public Action<EventId>? OnLog { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, eventId, exception));
            OnLog?.Invoke(eventId);
        }
    }

    private sealed class CompletingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MarkAsCompleteCallCount;
        public int DisposeCallCount;
        public TaskCompletionSource ConsumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => _completed.Task.IsCompleted;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task<DurableJobMutationResult> TryStartAttemptAsync(IJobRunContext jobContext, CancellationToken cancellationToken) =>
            Task.FromResult(DurableJobMutationResult.Applied);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref MarkAsCompleteCallCount);
            _completed.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.JobNotFound);

        public Task<DurableJobMutationResult> RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJobMutationResult> RescheduleJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) => Task.FromResult<DurableJob?>(null);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCallCount);
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<IJobRunContext> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ConsumeStarted.TrySetResult();
            await _completed.Task.WaitAsync(cancellationToken);
            yield break;
        }
    }

    private sealed class ControlledConsumingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        private readonly TaskCompletionSource<DurableJob> _scheduledJob = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ConsumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowConsume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => false;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(_scheduledJob.Task.IsCompleted ? 1 : 0);

        public Task<DurableJobMutationResult> TryStartAttemptAsync(IJobRunContext jobContext, CancellationToken cancellationToken) =>
            Task.FromResult(DurableJobMutationResult.Applied);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJobMutationResult> RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJobMutationResult> RescheduleJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            var job = new DurableJob
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = Id,
                Metadata = request.Metadata
            };

            _scheduledJob.SetResult(job);
            return Task.FromResult<DurableJob?>(job);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async IAsyncEnumerable<IJobRunContext> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ConsumeStarted.TrySetResult();
            var job = await _scheduledJob.Task.WaitAsync(cancellationToken);
            await AllowConsume.Task.WaitAsync(cancellationToken);
            yield return new JobRunContext(job, Guid.NewGuid().ToString(), retryCount: 1);
        }
    }

    private sealed class DisposedSchedulingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => true;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task<DurableJobMutationResult> TryStartAttemptAsync(IJobRunContext jobContext, CancellationToken cancellationToken) =>
            Task.FromResult(DurableJobMutationResult.Applied);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public Task<DurableJobMutationResult> RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public Task<DurableJobMutationResult> RescheduleJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<IJobRunContext> ConsumeAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class SchedulingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => false;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task<DurableJobMutationResult> TryStartAttemptAsync(IJobRunContext jobContext, CancellationToken cancellationToken) =>
            Task.FromResult(DurableJobMutationResult.Applied);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.JobNotFound);

        public Task<DurableJobMutationResult> RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJobMutationResult> RescheduleJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<DurableJob?>(new()
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = Id,
                Metadata = request.Metadata
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<IJobRunContext> ConsumeAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class GateableSchedulingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _completed;

        public TaskCompletionSource ScheduleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowScheduleToFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MarkAsCompleteCallCount;

        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => _completed;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task<DurableJobMutationResult> TryStartAttemptAsync(IJobRunContext jobContext, CancellationToken cancellationToken) =>
            Task.FromResult(DurableJobMutationResult.Applied);

        public async Task MarkAsCompleteAsync(CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                Interlocked.Increment(ref MarkAsCompleteCallCount);
                _completed = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.JobNotFound);

        public Task<DurableJobMutationResult> RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJobMutationResult> RescheduleJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public async Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                ScheduleStarted.TrySetResult();
                await AllowScheduleToFinish.Task.WaitAsync(cancellationToken);
                if (_completed)
                {
                    return null;
                }

                return new DurableJob
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = request.JobName,
                    DueTime = request.DueTime,
                    TargetGrainId = request.Target,
                    ShardId = Id,
                    Metadata = request.Metadata
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            _lock.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async IAsyncEnumerable<IJobRunContext> ConsumeAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestJobShardManager() : JobShardManager(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0))
    {
        public Func<DateTimeOffset, int, CancellationToken, IAsyncEnumerable<IJobShard>>? DiscoverShards { get; set; }

        internal override IAsyncEnumerable<IJobShard> DiscoverJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
            => DiscoverShards is { } discover
                ? discover(maxDueTime, maxNewClaims, cancellationToken)
                : base.DiscoverJobShardsAsync(maxDueTime, maxNewClaims, cancellationToken);

        public List<IJobShard> AssignedShards { get; } = [];

        public List<IJobShard> UnregisteredShards { get; } = [];

        public Func<DateTimeOffset, DateTimeOffset, IDictionary<string, string>, CancellationToken, Task<IJobShard>>? CreateShard { get; set; }

        public Func<string, CancellationToken, ValueTask<SiloAddress?>>? GetShardOwner { get; set; }

        public Func<string, CancellationToken, ValueTask<bool>>? IsShardOwned { get; set; }

        public Func<IJobShard, CancellationToken, Task>? UnregisterShard { get; set; }

        public bool IsLocallyOwned { get; set; } = true;

        public SiloAddress? ShardOwner { get; set; }

        public int CreateShardCallCount { get; private set; }

        public DateTimeOffset LastMaxDueTime { get; private set; }

        public override Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
        {
            LastMaxDueTime = maxDueTime;
            return Task.FromResult(AssignedShards.ToList());
        }

        public override Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            CreateShardCallCount++;
            return CreateShard is null
                ? throw new NotSupportedException()
                : CreateShard(minDueTime, maxDueTime, metadata, cancellationToken);
        }

        public override Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
        {
            UnregisteredShards.Add(shard);
            return UnregisterShard is { } unregister ? unregister(shard, cancellationToken) : Task.CompletedTask;
        }

        internal override ValueTask<SiloAddress?> GetShardOwnerAsync(string shardId, CancellationToken cancellationToken) =>
            GetShardOwner is { } getShardOwner
                ? getShardOwner(shardId, cancellationToken)
                : ValueTask.FromResult(ShardOwner);

        internal override ValueTask<bool> IsShardOwnedByLocalSiloAsync(string shardId, CancellationToken cancellationToken) =>
            IsShardOwned is { } isShardOwned
                ? isShardOwned(shardId, cancellationToken)
                : ValueTask.FromResult(IsLocallyOwned);
    }

    private sealed class TestLocalSiloDetails(SiloAddress siloAddress) : ILocalSiloDetails
    {
        public string Name => SiloAddress.ToParsableString();

        public string ClusterId => "TestCluster";

        public string DnsHostName => SiloAddress.ToParsableString();

        public SiloAddress SiloAddress { get; } = siloAddress;

        public SiloAddress GatewayAddress => SiloAddress;
    }

    private sealed class TestClusterMembershipService : IClusterMembershipService
    {
        private ImmutableDictionary<SiloAddress, ClusterMember> _members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty;
        private long _version;

        public ClusterMembershipSnapshot CurrentSnapshot => new(_members, new MembershipVersion(_version));

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => GetMembershipUpdates();

        public void SetSiloStatus(SiloAddress siloAddress, SiloStatus status)
        {
            _members = _members.SetItem(siloAddress, new ClusterMember(siloAddress, status, siloAddress.ToParsableString()));
            _version++;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        private static async IAsyncEnumerable<ClusterMembershipSnapshot> GetMembershipUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
