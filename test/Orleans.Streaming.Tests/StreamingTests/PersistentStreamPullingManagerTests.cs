using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using Orleans.Timers;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
public class PersistentStreamPullingManagerTests
{
    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData("initialized", false)]
    [InlineData("initialized", true)]
    [InlineData("stopped", false)]
    [InlineData("stopped", true)]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    [InlineData("canceled", false)]
    [InlineData("canceled", true)]
    public async Task RegisteredManager_IsDisposedByDirectoryAfterInitialization(string outcome, bool asyncDisposal)
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var instruments = new OrleansInstruments(services.GetRequiredService<IMeterFactory>());
        var timeProvider = new RecordingTimeProvider();
        var directory = new ActivationDirectory(new CatalogInstruments(instruments));
        var localSilo = Substitute.For<ILocalSiloDetails>();
        localSilo.SiloAddress.Returns(SiloAddress.New(IPAddress.Loopback, 11111, 1));
        var shared = new SystemTargetShared(
            runtimeClient: null!,
            localSilo,
            NullLoggerFactory.Instance,
            Options.Create(new SchedulingOptions()),
            grainReferenceActivator: null!,
            new TimerRegistry(NullLoggerFactory.Instance, timeProvider),
            directory,
            new SchedulerInstruments(instruments),
            new GrainInstruments(instruments),
            new MessagingInstruments(instruments),
            new MessagingProcessingInstruments(instruments));
        var balancer = Substitute.For<IStreamQueueBalancer>();
        balancer.GetMyQueues().Returns(Array.Empty<QueueId>());
        var initializationFailure = new InvalidOperationException("Queue balancer initialization failed.");
        balancer.Initialize(Arg.Any<IStreamQueueMapper>()).Returns(
            outcome == "failed" ? Task.FromException(initializationFailure) : Task.CompletedTask);
        var manager = new PersistentStreamPullingManager(
            SystemTargetGrainId.Create(Constants.StreamPullingAgentManagerType, shared.SiloAddress, "provider"),
            "provider",
            Substitute.For<IStreamPubSub>(),
            Substitute.For<IQueueAdapterFactory>(),
            balancer,
            new NoOpStreamFilter(),
            new StreamPullingAgentOptions(),
            Substitute.For<IQueueAdapter>(),
            new FixedBackoff(TimeSpan.FromSeconds(1)),
            new FixedBackoff(TimeSpan.FromSeconds(1)),
            timeProvider,
            new StreamInstruments(instruments),
            shared);
        try
        {
            Assert.Same(manager, directory.FindTarget(manager.GrainId));
            Assert.Equal(1, directory.Count);
            // A timer owned before initialization observes directory disposal even when initialization creates no timer.
            manager.RegisterTimer(_ => Task.CompletedTask, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var lifetimeProbe = Assert.Single(timeProvider.Timers);

            if (outcome == "failed")
            {
                Assert.Same(initializationFailure, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => manager.Initialize(TestContext.Current.CancellationToken)));
                Assert.Single(timeProvider.Timers);
            }
            else if (outcome == "canceled")
            {
                var cancellation = new CancellationToken(canceled: true);
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.Initialize(cancellation));
                Assert.Equal(cancellation, exception.CancellationToken);
                await balancer.DidNotReceive().Initialize(Arg.Any<IStreamQueueMapper>());
                Assert.Single(timeProvider.Timers);
            }
            else
            {
                await manager.Initialize(TestContext.Current.CancellationToken);
                Assert.Equal(2, timeProvider.Timers.Count);
                var timer = timeProvider.Timers[1];
                Assert.Equal(0, timer.DisposeCount);
                if (outcome == "stopped")
                {
                    await manager.Stop(TestContext.Current.CancellationToken);
                    await manager.Stop(TestContext.Current.CancellationToken);
                    await balancer.Received(1).Shutdown();
                    Assert.Equal(1, timer.DisposeCount);
                }
            }

            // Stopping provider work and completing initialization leave final target ownership with the directory.
            Assert.Same(manager, directory.FindTarget(manager.GrainId));
            Assert.Equal(0, lifetimeProbe.DisposeCount);
            for (var iteration = 0; iteration < 2; iteration++)
            {
                if (asyncDisposal)
                {
                    await ((IAsyncDisposable)directory).DisposeAsync();
                }
                else
                {
                    ((IDisposable)directory).Dispose();
                }

                Assert.Null(directory.FindTarget(manager.GrainId));
                Assert.Equal(0, directory.Count);
                Assert.All(timeProvider.Timers, timer => Assert.Equal(1, timer.DisposeCount));
            }
        }
        finally
        {
            await ((IAsyncDisposable)directory).DisposeAsync();
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<RecordingTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RecordingTimer();
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class RecordingTimer : ITimer
    {
        public int DisposeCount { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => DisposeCount == 0;
        public void Dispose() => DisposeCount++;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
