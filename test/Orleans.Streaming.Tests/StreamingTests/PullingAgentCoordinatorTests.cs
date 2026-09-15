using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Placement;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public sealed class PullingAgentCoordinatorTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    public async Task ColdStart_UsesBalancedHintsAndProbesRemainSticky(int queueCount, short siloCount)
    {
        await using var setup = new Setup(queueCount, siloCount);
        await setup.Deploy();
        foreach (var silo in setup.Cluster.Silos)
        {
            await setup.StartProvider(silo);
        }

        var initialized = setup.WaitForStarts(queueCount);
        var original = await setup.Round();
        await initialized;
        AssertBalanced(original, setup.Cluster.Silos.Select(silo => silo.SiloAddress));
        Assert.Equal(queueCount, setup.Initializations);
        foreach (var status in original.Values)
        {
            var other = setup.Cluster.Silos.First(silo => silo.SiloAddress != status.Address.SiloAddress).SiloAddress;
            RequestContext.Set(IPlacementDirector.PlacementHintKey, other);
            try
            {
                var probed = await setup.Agent(status.Address.GrainId).Probe(TestContext.Current.CancellationToken);
                Assert.True(probed.IsRunning);
                Assert.Equal(status.Address, probed.Address);
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }
        }

        AssertSameAgents(original, await setup.Round());
        setup.Clock.Advance(TimeSpan.FromMinutes(2));
        AssertSameAgents(original, await setup.Round());
        Assert.Equal(queueCount, setup.Initializations);
        Assert.Equal(0, setup.Shutdowns);
    }

    [Fact]
    public async Task EligibleHostExpansion_WaitsSixtySecondsThenMakesOnlyMinimalMoves()
    {
        await using var setup = new Setup(queueCount: 6, siloCount: 2);
        await setup.Deploy();
        var a = setup.Cluster.Silos[0];
        var b = setup.Cluster.Silos[1];
        await setup.StartProvider(a);
        var initialized = setup.WaitForStarts(6);
        var original = await setup.Round();
        await initialized;
        Assert.All(original.Values, value => Assert.Equal(a.SiloAddress, value.Address.SiloAddress));

        await setup.StartProvider(b);
        AssertSameAgents(original, await setup.Round());
        setup.Clock.Advance(TimeSpan.FromSeconds(59));
        AssertSameAgents(original, await setup.Round());
        Assert.Equal(6, setup.Initializations);
        Assert.Equal(0, setup.Shutdowns);

        var moved = setup.WaitForStarts(3);
        setup.Clock.Advance(TimeSpan.FromSeconds(1));
        await setup.Round();
        await moved;
        var balanced = await setup.Round();
        Assert.Equal(3, balanced.Values.Count(value => value.Address.SiloAddress == a.SiloAddress));
        Assert.Equal(3, balanced.Values.Count(value => value.Address.SiloAddress == b.SiloAddress));
        Assert.Equal(3, original.Count(entry => !entry.Value.Address.Equals(balanced[entry.Key].Address)));
        Assert.Equal(9, setup.Initializations);
        Assert.Equal(3, setup.Shutdowns);

        setup.Clock.Advance(TimeSpan.FromMinutes(2));
        AssertSameAgents(balanced, await setup.Round());
        AssertSameAgents(balanced, await setup.Round());
        Assert.Equal(9, setup.Initializations);
        Assert.Equal(3, setup.Shutdowns);
    }

    [Fact]
    public async Task EligibleHostSetChange_RestartsStabilizationDelay()
    {
        await using var setup = new Setup(queueCount: 6, siloCount: 3);
        await setup.Deploy();
        await setup.StartProvider(setup.Cluster.Silos[0]);
        var initialized = setup.WaitForStarts(6);
        var original = await setup.Round();
        await initialized;

        await setup.StartProvider(setup.Cluster.Silos[1]);
        AssertSameAgents(original, await setup.Round());
        setup.Clock.Advance(TimeSpan.FromSeconds(30));
        AssertSameAgents(original, await setup.Round());
        await setup.StartProvider(setup.Cluster.Silos[2]);
        AssertSameAgents(original, await setup.Round());

        setup.Clock.Advance(TimeSpan.FromSeconds(30));
        AssertSameAgents(original, await setup.Round());
        setup.Clock.Advance(TimeSpan.FromSeconds(29));
        AssertSameAgents(original, await setup.Round());
        Assert.Equal(6, setup.Initializations);
        Assert.Equal(0, setup.Shutdowns);

        var moved = setup.WaitForStarts(4);
        setup.Clock.Advance(TimeSpan.FromSeconds(1));
        await setup.Round();
        await moved;
        var balanced = await setup.Round();
        AssertBalanced(balanced, setup.Cluster.Silos.Select(silo => silo.SiloAddress));
        Assert.Equal(4, original.Count(entry => !entry.Value.Address.Equals(balanced[entry.Key].Address)));
        Assert.Equal(10, setup.Initializations);
        Assert.Equal(4, setup.Shutdowns);
        setup.Clock.Advance(TimeSpan.FromMinutes(1));
        AssertSameAgents(balanced, await setup.Round());
        Assert.Equal(10, setup.Initializations);
    }

    [Fact]
    public async Task CoordinatorHeartbeatRecovery_PreservesAgentsAndMissingAgentRecoveryIsImmediate()
    {
        await using var setup = new Setup(queueCount: 4, siloCount: 2);
        await setup.Deploy();
        foreach (var silo in setup.Cluster.Silos)
        {
            await setup.StartProvider(silo);
        }

        var initialized = setup.WaitForStarts(4);
        var original = await setup.Round();
        await initialized;
        Assert.True(setup.Cluster.TryGetGrainContext(setup.CoordinatorId, out var originalCoordinator));
        var originalCoordinatorAddress = originalCoordinator.Address;
        await setup.Cluster.DeactivateAsync(setup.CoordinatorId).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        var reactivated = setup.Events.WaitForEventAsync(
            nameof(GrainLifecycleEvents.Activated),
            evt => evt.Payload is GrainLifecycleEvents.Activated value
                && value.GrainContext.GrainId == setup.CoordinatorId
                && value.GrainContext.ActivationId != originalCoordinatorAddress.ActivationId,
            PhaseTimeout, TestContext.Current.CancellationToken);

        // No coordinator or agent RPC is issued until the local heartbeat has recreated the coordinator.
        setup.Clock.Advance(TimeSpan.FromSeconds(30));
        await reactivated;
        AssertSameAgents(original, await setup.Round());
        Assert.Equal(4, setup.Initializations);
        Assert.Equal(0, setup.Shutdowns);

        var queue = setup.Queues[0];
        var lostAgent = original[queue].Address;
        await setup.Cluster.DeactivateAsync(lostAgent.GrainId).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        var timestamp = setup.Clock.GetTimestamp();
        var recovered = setup.WaitForStarts(1);
        var current = await setup.Round();
        await recovered;
        Assert.Equal(timestamp, setup.Clock.GetTimestamp());
        Assert.True(current[queue].IsRunning);
        Assert.Equal(lostAgent.GrainId, current[queue].Address.GrainId);
        Assert.Equal(lostAgent.SiloAddress, current[queue].Address.SiloAddress);
        Assert.NotEqual(lostAgent.ActivationId, current[queue].Address.ActivationId);
        foreach (var entry in original.Where(entry => entry.Key != queue))
        {
            Assert.Equal(entry.Value.Address, current[entry.Key].Address);
        }

        Assert.Equal(5, setup.Initializations);
        Assert.Equal(1, setup.Shutdowns);
    }

    [Fact]
    public async Task GracefulShutdown_MigratesAgentsToRunningSurvivor()
    {
        await using var setup = new Setup(queueCount: 2, siloCount: 2);
        await setup.Deploy();
        foreach (var silo in setup.Cluster.Silos)
        {
            await setup.StartProvider(silo);
        }

        var initialized = setup.WaitForStarts(2);
        var original = await setup.Round();
        await initialized;
        var source = setup.Cluster.Silos[0];
        var survivor = setup.Cluster.Silos[1];
        var moving = Assert.Single(original.Values, status => status.Address.SiloAddress == source.SiloAddress);
        var staying = Assert.Single(original.Values, status => status.Address.SiloAddress == survivor.SiloAddress);
        var migrated = setup.WaitForStarts(1);
        await setup.Cluster.StopSiloAsync(source, TestContext.Current.CancellationToken).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        await migrated;
        await setup.Cluster.WaitForLivenessToStabilizeAsync().WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        var result = await setup.Agent(moving.Address.GrainId).Probe(TestContext.Current.CancellationToken);
        Assert.True(result.IsRunning);
        Assert.Equal(survivor.SiloAddress, result.Address.SiloAddress);
        Assert.Equal(moving.Address.GrainId, result.Address.GrainId);
        Assert.NotEqual(moving.Address.ActivationId, result.Address.ActivationId);
        Assert.Equal(staying.Address, (await setup.Agent(staying.Address.GrainId).Probe(TestContext.Current.CancellationToken)).Address);
        Assert.Equal(3, setup.Initializations);
        Assert.Equal(1, setup.Shutdowns);
    }

    private static void AssertBalanced(Dictionary<QueueId, PullingAgentStatus> agents, IEnumerable<SiloAddress> eligible)
    {
        Assert.All(agents.Values, status => Assert.True(status.IsRunning));
        var counts = eligible.Select(silo => agents.Values.Count(status => status.Address.SiloAddress == silo)).Order().ToArray();
        var low = agents.Count / counts.Length;
        var highCount = agents.Count % counts.Length;
        Assert.Equal(Enumerable.Repeat(low, counts.Length - highCount).Concat(Enumerable.Repeat(low + 1, highCount)), counts);
    }

    private static void AssertSameAgents(Dictionary<QueueId, PullingAgentStatus> expected, Dictionary<QueueId, PullingAgentStatus> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var entry in expected)
        {
            Assert.True(actual[entry.Key].IsRunning);
            Assert.Equal(entry.Value.Address, actual[entry.Key].Address);
        }
    }

    private sealed class Setup : IAsyncDisposable
    {
        private const string ProviderName = "pulling-agent-coordinator-tests";
        private readonly Channel<GrainAddress> _starts = Channel.CreateUnbounded<GrainAddress>();
        private int _initializations;
        private int _shutdowns;
        internal DrivenFakeTimeProvider Clock { get; } = new();
        internal DiagnosticEventCollector Events { get; } = new(GrainLifecycleEvents.ListenerName, GrainTimerEvents.ListenerName);
        internal InProcessTestCluster Cluster { get; }
        internal QueueId[] Queues { get; }
        internal GrainId CoordinatorId => PullingAgentCoordinatorGrain.GetGrainId(ProviderName);
        internal IPullingAgentCoordinatorGrain Coordinator => Cluster.Client.GetGrain<IPullingAgentCoordinatorGrain>(CoordinatorId);
        internal int Initializations => Volatile.Read(ref _initializations);
        internal int Shutdowns => Volatile.Read(ref _shutdowns);
        internal IPullingAgentGrain Agent(GrainId id) => Cluster.Client.GetGrain<IPullingAgentGrain>(id);

        internal Setup(int queueCount, short siloCount)
        {
            Queues = Enumerable.Range(0, queueCount).Select(index => QueueId.GetQueueId("coordinator", (uint)index, (uint)index)).ToArray();
            var builder = new InProcessTestClusterBuilder(siloCount);
#pragma warning disable ORLEANSEXP003
            builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003
            builder.ConfigureSilo((_, silo) =>
            {
                silo.Services.AddSingleton<TimeProvider>(Clock);
                silo.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);
                silo.AddPersistentStreams(ProviderName, CreateFactory, configurator =>
                {
                    configurator.ConfigurePullingAgent(options => options.Configure(value =>
                    {
                        value.HostingMode = StreamPullingAgentHostingMode.Grain;
                        value.GrainHostingProbePeriod = TimeSpan.FromSeconds(30);
                        value.GrainHostingRebalanceDelay = TimeSpan.FromMinutes(1);
                        value.GetQueueMsgsTimerPeriod = TimeSpan.FromSeconds(1);
                    }));
                    configurator.ConfigureLifecycle(options => options.Configure(value => value.StartupState = StreamLifecycleOptions.RunState.AgentsStopped));
                    configurator.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    configurator.ConfigurePartitionBalancing((_, _) =>
                        throw new InvalidOperationException("Grain hosting must not construct a queue balancer."));
                });
            });
            Cluster = builder.Build();
        }

        internal async Task Deploy()
        {
            await Cluster.DeployAsync(TestContext.Current.CancellationToken);
            await Cluster.WaitForLivenessToStabilizeAsync().WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            await Cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }

        internal Task StartProvider(InProcessSiloHandle silo)
            => silo.ServiceProvider.GetRequiredKeyedService<IControllable>(ProviderName)
                .ExecuteCommand((int)PersistentStreamProviderCommand.StartAgents, null)
                .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);

        internal async Task<Dictionary<QueueId, PullingAgentStatus>> Round()
        {
            await Coordinator.GetAgents(TestContext.Current.CancellationToken).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            var previous = Events.GetEvents(nameof(GrainTimerEvents.TickStop)).Select(evt => evt.Payload).ToHashSet();
            var completed = Events.WaitForEventAsync(
                nameof(GrainTimerEvents.TickStop),
                evt => evt.Payload is GrainTimerEvents.TickStop value
                    && value.GrainContext.GrainId == CoordinatorId && !previous.Contains(value),
                PhaseTimeout, TestContext.Current.CancellationToken);
            await Coordinator.NotifyHostChanged(TestContext.Current.CancellationToken).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            Clock.Advance(TimeSpan.Zero);
            var stopped = await completed;
            Assert.Null(Assert.IsType<GrainTimerEvents.TickStop>(stopped.Payload).Exception);
            return await Coordinator.GetAgents(TestContext.Current.CancellationToken).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }

        internal async Task WaitForStarts(int count)
        {
            for (var index = 0; index < count; index++)
            {
                var address = await _starts.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                    .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
                await Events.WaitForEventAsync(nameof(GrainLifecycleEvents.Activated),
                    evt => evt.Payload is GrainLifecycleEvents.Activated value && value.GrainContext.Address.Equals(address),
                    PhaseTimeout, TestContext.Current.CancellationToken);
            }
        }

        private IQueueAdapterFactory CreateFactory(IServiceProvider services, string name)
        {
            var context = services.GetRequiredService<IGrainContextAccessor>();
            var mapper = Substitute.For<IStreamQueueMapper>();
            mapper.GetAllQueues().Returns(Queues);
            mapper.GetQueueForStream(Arg.Any<StreamId>()).Returns(Queues[0]);
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(_ =>
            {
                var cache = Substitute.For<IQueueCache>();
                cache.GetMaxAddCount().Returns(100);
                return cache;
            });
            var adapter = Substitute.For<IQueueAdapter>();
            adapter.Name.Returns(name);
            adapter.Direction.Returns(StreamProviderDirection.ReadOnly);
            adapter.CreateReceiver(Arg.Any<QueueId>()).Returns(_ =>
            {
                var address = context.GrainContext.Address;
                var receiver = Substitute.For<IQueueAdapterReceiver>();
                receiver.Initialize(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    Interlocked.Increment(ref _initializations);
                    Assert.True(_starts.Writer.TryWrite(address));
                    return Task.CompletedTask;
                });
                receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IList<IBatchContainer>>([]));
                receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    Interlocked.Increment(ref _shutdowns);
                    return Task.CompletedTask;
                });
                return receiver;
            });
            var factory = Substitute.For<IQueueAdapterFactory>();
            factory.CreateAdapter(Arg.Any<CancellationToken>()).Returns(Task.FromResult(adapter));
            factory.GetStreamQueueMapper().Returns(mapper);
            factory.GetQueueAdapterCache().Returns(adapterCache);
            factory.GetDeliveryFailureHandler(Arg.Any<QueueId>())
                .Returns(Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler()));
            return factory;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Cluster.DisposeAsync();
            }
            finally
            {
                Events.Dispose();
            }
        }

        // Zero-due changes also wait for the single test driver, so starting providers cannot race cold placement.
        internal sealed class DrivenFakeTimeProvider : FakeTimeProvider
        {
            private readonly ConcurrentQueue<DrivenTimer> _ready = new();

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                var timer = new DrivenTimer(this, callback, state);
                timer.Inner = base.CreateTimer(static value => ((DrivenTimer)value!).Queue(), timer, dueTime, period);
                return timer;
            }

            internal new void Advance(TimeSpan delta)
            {
                base.Advance(delta);
                while (_ready.TryDequeue(out var timer))
                {
                    timer.Fire();
                }
            }

            private sealed class DrivenTimer(DrivenFakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
            {
                private int _pending;
                private int _disposed;
                internal ITimer Inner { get; set; } = null!;

                internal void Queue()
                {
                    if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _pending, 1) == 0)
                    {
                        owner._ready.Enqueue(this);
                    }
                }

                internal void Fire()
                {
                    if (Interlocked.Exchange(ref _pending, 0) != 0 && Volatile.Read(ref _disposed) == 0)
                    {
                        callback(state);
                    }
                }

                public bool Change(TimeSpan dueTime, TimeSpan period)
                {
                    Interlocked.Exchange(ref _pending, 0);
                    return Inner.Change(dueTime, period);
                }

                public void Dispose()
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    Inner.Dispose();
                }

                public ValueTask DisposeAsync()
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    return Inner.DisposeAsync();
                }
            }
        }
    }
}
