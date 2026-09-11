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
        Assert.Empty(shardManager.UnregisteredShards);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_WhenDiscoveryActivatesShardDuringShutdown_AwaitsShardCleanup(bool failDiscoveryDisposal)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, CreateOptions());
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

        var shard = new BlockingQueueShard("final-activation", timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddHours(1));
        var discoveredShard = Substitute.For<IJobShard>();
        Task? stop = null;
        Task? running = null;
        discoveredShard.Id.Returns(shard.Id);
        discoveredShard.StartTime.Returns(_ =>
        {
            // The yielded shard has passed its cancellation check, but has not been activated yet.
            stop ??= observer.OnStop(cancellationToken);
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
                // Let activation finish publishing its running task before observing cleanup.
                discoveryDisposed.SetResult(manager.QueueTask(() =>
                {
                    Assert.True(accessor.TryGetRunningShardTask(shard.Id, out running));
                    Assert.NotNull(running);
                    Assert.False(running.IsCompleted);
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

            if (running is not null)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        Assert.Equal(1, Volatile.Read(ref disposalCount));
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(accessor.HasCachedShard(undeliveredShard.Id));
        await undeliveredShard.Received(1).DisposeAsync();
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
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
    public async Task Stop_WhenShardActivatesAfterInitialSnapshot_AwaitsShardCleanup()
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
        Task? running = null;
        try
        {
            Assert.Equal(1, Volatile.Read(ref activationCount));
            Assert.True(accessor.TryGetRunningShardTask(shard.Id, out running));
            Assert.NotNull(running);
            Assert.False(stop.IsCompleted);
            await shard.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(running.IsCompleted);
            Assert.Equal(0, shard.DisposeCallCount);
        }
        finally
        {
            shard.AllowDispose.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (running is not null)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        Assert.Equal(1, shard.DisposeCallCount);
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
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
    }

    private static LocalDurableJobManager CreateManager(
        JobShardManager shardManager,
        FakeTimeProvider timeProvider,
        DurableJobsOptions options,
        IInternalGrainFactory? grainFactory = null,
        ILogger<LocalDurableJobManager>? logger = null)
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
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

        public Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.JobNotFound);

        public Task<DurableJobMutationResult> RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJobMutationResult> RescheduleJobAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.FromResult(DurableJobMutationResult.Applied);

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) => Task.FromResult<DurableJob?>(null);

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
            return Task.CompletedTask;
        }

        internal override ValueTask<SiloAddress?> GetShardOwnerAsync(string shardId, CancellationToken cancellationToken) =>
            GetShardOwner is { } getShardOwner
                ? getShardOwner(shardId, cancellationToken)
                : ValueTask.FromResult(ShardOwner);

        internal override ValueTask<bool> IsShardOwnedByLocalSiloAsync(string shardId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(IsLocallyOwned);
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
