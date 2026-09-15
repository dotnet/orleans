using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public sealed class GrainHostedPullingAgentControlTests
{
    private const string ProviderName = "grain-hosted-control";
    private static readonly QueueId Queue = QueueId.GetQueueId("Control", 0, 1);
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task StartupStopped_RequiresStartAndRemainsStoppedAfterCallbacksAndReconciliation()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        setup.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, setup.Initializations);
        Assert.Equal(StreamLifecycleOptions.RunState.Initialized, await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));

        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        Assert.Equal(silo.SiloAddress, await setup.NextInitialization());
        Assert.Equal(1, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
        await setup.Command(silo, PersistentStreamProviderCommand.StopAgents);
        var readsAtStop = setup.Reads;

        var grain = setup.Cluster.Client.GetGrain<IGrainHostedStreamPullingAgent>(StreamPullingAgentId.Create(ProviderName, Queue));
        await grain.AddSubscriber(
            GuidId.GetGuidId(Guid.NewGuid()),
            new QualifiedStreamId(ProviderName, StreamId.Create("namespace", "stream")),
            GrainId.Create("consumer", "stopped"),
            null,
            TestContext.Current.CancellationToken);
        Assert.False((await grain.Probe(TestContext.Current.CancellationToken)).IsRunning);
        await setup.Coordinator.NotifyHostChanged(TestContext.Current.CancellationToken);
        setup.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, setup.Initializations);
        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(readsAtStop, setup.Reads);
        Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped, await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));

        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        Assert.Equal(silo.SiloAddress, await setup.NextInitialization());
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(1, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task LifecycleStop_DrainsAgentsWithoutSendingCoordinatorNotification()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        Assert.Equal(1, setup.CoordinatorNotifications);

        var managerId = SystemTargetGrainId.Create(Constants.StreamPullingAgentManagerType, silo.SiloAddress, ProviderName);
        var manager = silo.ServiceProvider.GetRequiredService<IInternalGrainFactory>()
            .GetSystemTarget<IPersistentStreamPullingManager>(managerId.GrainId);
        await manager.Stop(TestContext.Current.CancellationToken).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(1, setup.CoordinatorNotifications);
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task StopAgents_DrainsActivationTriggeredWhileReceiverIsInitializing()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var grainId = StreamPullingAgentId.Create(ProviderName, Queue);
        await setup.Cluster.DeactivateAsync(grainId).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);

        setup.InitializationBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = setup.Cluster.Client.GetGrain<IGrainHostedStreamPullingAgent>(grainId)
            .Probe(TestContext.Current.CancellationToken);
        await setup.NextInitialization();
        var stopping = setup.Command(silo, PersistentStreamProviderCommand.StopAgents);
        try
        {
            Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped, await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
            Assert.False(stopping.IsCompleted);
            Assert.False(activation.IsCompleted);
        }
        finally
        {
            setup.InitializationBarrier.TrySetResult();
            await activation.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            await stopping;
        }

        var readsAtStop = setup.Reads;
        setup.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(2, setup.Shutdowns);
        Assert.Equal(readsAtStop, setup.Reads);
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task StopAgents_ReportsZeroRunningAgentsWhileAwaitingReceiverShutdown()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        setup.ShutdownBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = setup.Command(silo, PersistentStreamProviderCommand.StopAgents);
        try
        {
            await setup.ShutdownEntered.Task.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
            Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped, await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
        }
        finally
        {
            setup.ShutdownBarrier.TrySetResult();
            await stopping;
        }

        Assert.Equal(1, setup.Shutdowns);
    }

    [Fact]
    public async Task ReconciliationTimer_ReactivatesQueueWithoutExternalGrainTraffic()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        var meter = silo.ServiceProvider.GetRequiredService<OrleansInstruments>().Meter;
        var cacheGaugeRegistrations = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && instrument.Name == InstrumentNames.STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE)
                {
                    Interlocked.Increment(ref cacheGaugeRegistrations);
                }
            },
        };
        listener.Start();
        Assert.Equal(1, cacheGaugeRegistrations);
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var grainId = StreamPullingAgentId.Create(ProviderName, Queue);
        Assert.True(setup.Cluster.TryGetGrainContext(grainId, out var original));
        var originalAddress = original.Address;

        await setup.Cluster.DeactivateAsync(grainId).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));

        setup.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(silo.SiloAddress, await setup.NextInitialization());

        // The initialization above was triggered by supervision; this call observes its completed activation.
        var status = await setup.Cluster.Client.GetGrain<IGrainHostedStreamPullingAgent>(grainId)
            .Probe(TestContext.Current.CancellationToken);
        var address = status.Address;
        Assert.True(status.IsRunning);
        Assert.Equal(grainId, address.GrainId);
        Assert.Equal(originalAddress.SiloAddress, address.SiloAddress);
        Assert.NotEqual(originalAddress.ActivationId, address.ActivationId);
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(1, cacheGaugeRegistrations);
        Assert.Equal(1, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task StopOnPreviousHost_LeavesMovedActivationRunning()
    {
        await using var setup = new Setup(2);
        await setup.Deploy();
        var source = setup.Cluster.Silos[0];
        var destination = setup.Cluster.Silos[1];
        await setup.Command(source, PersistentStreamProviderCommand.StartAgents);
        await setup.Command(destination, PersistentStreamProviderCommand.StartAgents);
        Assert.Equal(source.SiloAddress, await setup.NextInitialization());

        var grain = setup.Cluster.Client.GetGrain<IGrainHostedStreamPullingAgent>(StreamPullingAgentId.Create(ProviderName, Queue));
        var previous = await grain.Probe(TestContext.Current.CancellationToken);
        Assert.True(await grain.Rebalance(previous.Address, destination.SiloAddress, TestContext.Current.CancellationToken));
        Assert.Equal(destination.SiloAddress, await setup.NextInitialization());
        var address = (await grain.Probe(TestContext.Current.CancellationToken)).Address;
        Assert.False(await grain.Rebalance(previous.Address, source.SiloAddress, TestContext.Current.CancellationToken));
        await grain.Stop(source.SiloAddress, TestContext.Current.CancellationToken);
        await setup.Command(source, PersistentStreamProviderCommand.StopAgents);

        Assert.Equal(destination.SiloAddress, address.SiloAddress);
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(source, PersistentStreamProviderCommand.GetNumberRunningAgents));
        Assert.Equal(1, await setup.Command(destination, PersistentStreamProviderCommand.GetNumberRunningAgents));
        Assert.Equal(StreamLifecycleOptions.RunState.AgentsStarted, await setup.Command(destination, PersistentStreamProviderCommand.GetAgentsState));

        await setup.Command(destination, PersistentStreamProviderCommand.StopAgents);
        Assert.Equal(2, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(destination, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    private sealed class Setup : IAsyncDisposable
    {
        private readonly Channel<SiloAddress> _initialized = Channel.CreateUnbounded<SiloAddress>();
        private int _initializations;
        private int _shutdowns;
        private int _reads;
        private int _coordinatorNotifications;

        internal InProcessTestCluster Cluster { get; }
        internal IStreamPullingAgentCoordinator Coordinator => Cluster.Client.GetGrain<IStreamPullingAgentCoordinator>(
            StreamPullingAgentCoordinator.GetGrainId(ProviderName));
        internal FakeTimeProvider Clock { get; } = new();
        internal TaskCompletionSource ShutdownEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? ShutdownBarrier { get; set; }
        internal TaskCompletionSource? InitializationBarrier { get; set; }
        internal int Initializations => Volatile.Read(ref _initializations);
        internal int Shutdowns => Volatile.Read(ref _shutdowns);
        internal int Reads => Volatile.Read(ref _reads);
        internal int CoordinatorNotifications => Volatile.Read(ref _coordinatorNotifications);

        internal Setup(short siloCount)
        {
            var builder = new InProcessTestClusterBuilder(siloCount);
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.Services.AddSingleton<TimeProvider>(Clock);
                siloBuilder.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);
                siloBuilder.Services.AddSingleton<IOutgoingGrainCallFilter>(new NotificationRecorder(this));
                siloBuilder.AddPersistentStreams(
                    ProviderName,
                    (services, name) => CreateFactory(name, services.GetRequiredService<ILocalSiloDetails>().SiloAddress),
                    configurator =>
                    {
                        configurator.ConfigurePullingAgent(options => options.Configure(value =>
                        {
                            value.HostingMode = StreamPullingAgentHostingMode.Grain;
                            value.GetQueueMsgsTimerPeriod = TimeSpan.FromSeconds(1);
                        }));
                        configurator.ConfigureLifecycle(options => options.Configure(value =>
                            value.StartupState = StreamLifecycleOptions.RunState.AgentsStopped));
                        configurator.ConfigurePartitionBalancing((_, _) =>
                            throw new InvalidOperationException("Grain hosting must not construct a queue balancer."));
                    });
                siloBuilder.Services.Configure<StreamPubSubOptions>(ProviderName, options => options.PubSubType = StreamPubSubType.ImplicitOnly);
            });
            Cluster = builder.Build();
        }

        internal async Task Deploy()
        {
            await Cluster.DeployAsync(TestContext.Current.CancellationToken);
            await Cluster.WaitForLivenessToStabilizeAsync().WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            await Cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }

        internal async Task<SiloAddress> NextInitialization()
        {
            try
            {
                return await _initialized.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                    .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Waiting for receiver initialization for {ProviderName}/{Queue}; initialized={Initializations}, shutdown={Shutdowns}, reads={Reads}.",
                    exception);
            }
        }

        internal async Task<object?> Command(InProcessSiloHandle silo, PersistentStreamProviderCommand command)
        {
            var result = await silo.ServiceProvider.GetRequiredKeyedService<IControllable>(ProviderName).ExecuteCommand((int)command, null)
                .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            if (command == PersistentStreamProviderCommand.StartAgents)
            {
                Clock.Advance(TimeSpan.Zero);
                await Coordinator.GetAgents(TestContext.Current.CancellationToken).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            }

            return result;
        }

        private IQueueAdapterFactory CreateFactory(string name, SiloAddress silo)
        {
            var mapper = Substitute.For<IStreamQueueMapper>();
            mapper.GetAllQueues().Returns([Queue]);
            mapper.GetQueueForStream(Arg.Any<StreamId>()).Returns(Queue);
            var cache = Substitute.For<IQueueCache>();
            cache.GetMaxAddCount().Returns(100);
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(Queue).Returns(cache);
            var adapter = Substitute.For<IQueueAdapter>();
            adapter.Name.Returns(name);
            adapter.Direction.Returns(StreamProviderDirection.ReadOnly);
            adapter.IsRewindable.Returns(true);
            adapter.CreateReceiver(Queue).Returns(_ =>
            {
                var receiver = Substitute.For<IQueueAdapterReceiver>();
                receiver.Initialize(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    Interlocked.Increment(ref _initializations);
                    Assert.True(_initialized.Writer.TryWrite(silo));
                    return InitializationBarrier?.Task ?? Task.CompletedTask;
                });
                receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    Interlocked.Increment(ref _reads);
                    return Task.FromResult<IList<IBatchContainer>>([]);
                });
                receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    Interlocked.Increment(ref _shutdowns);
                    ShutdownEntered.TrySetResult();
                    return ShutdownBarrier?.Task ?? Task.CompletedTask;
                });
                return receiver;
            });

            var factory = Substitute.For<IQueueAdapterFactory>();
            factory.CreateAdapter(Arg.Any<CancellationToken>()).Returns(Task.FromResult(adapter));
            factory.GetStreamQueueMapper().Returns(mapper);
            factory.GetQueueAdapterCache().Returns(adapterCache);
            factory.GetDeliveryFailureHandler(Queue).Returns(Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler()));
            return factory;
        }

        public ValueTask DisposeAsync() => Cluster.DisposeAsync();

        private sealed class NotificationRecorder(Setup setup) : IOutgoingGrainCallFilter
        {
            public Task Invoke(IOutgoingGrainCallContext context)
            {
                if (context.TargetId.Equals(StreamPullingAgentCoordinator.GetGrainId(ProviderName))
                    && context.MethodName == nameof(IStreamPullingAgentCoordinator.NotifyHostChanged))
                {
                    Interlocked.Increment(ref setup._coordinatorNotifications);
                }

                return context.Invoke();
            }
        }
    }

}
