using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Orleans.Hosting;
using Orleans.Diagnostics;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Placement;
using Orleans.Serialization;
using Orleans.Statistics;
using Orleans.Streaming.Diagnostics;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("EventHub"), TestCategory("Streaming")]
public sealed class EventHubGrainMigrationTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task GrainHosting_SiloShutdownFlushesDuringDeactivationAndPreservesProducer()
        => RunGracefulShutdown(failFlush: false);

    [Fact]
    public Task GrainHosting_FailedDeactivationFlushSurfacesFailureAndSuppressesMigration()
        => RunGracefulShutdown(failFlush: true);

    [Fact]
    public async Task GrainHosting_RebalancesEventHubReceiverFromAToBToAAfterCheckpointFlush()
    {
        var state = new EventHubMigrationState();
        await using var cluster = CreateCluster(state);
        using var events = new DiagnosticEventCollector(
            StreamingEvents.ListenerName, GrainLifecycleEvents.ListenerName, GrainTimerEvents.ListenerName);
        try
        {
            await Deploy(cluster);
            var siloA = cluster.Silos[0].SiloAddress;
            var siloB = cluster.Silos[1].SiloAddress;
            var consumer = cluster.Client.GetGrain<IEventHubMigrationConsumer>(Guid.NewGuid());
            var relayA = cluster.Client.GetGrain<IEventHubMigrationRelay>(Guid.NewGuid());
            var relayB = cluster.Client.GetGrain<IEventHubMigrationRelay>(Guid.NewGuid());
            await Place(relayA, siloA);
            await Place(relayB, siloB);
            RequestContext.Set(IPlacementDirector.PlacementHintKey, siloB);
            try
            {
                Assert.Equal(siloB, await Wait(consumer.Subscribe(), "Event Hubs consumer subscription"));
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            var initialDelivery = WaitForDrain(events, state, siloA);
            await Wait(cluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IControllable>(EventHubMigrationState.ProviderName)
                .ExecuteCommand((int)PersistentStreamProviderCommand.StartAgents, null), "starting Event Hubs provider on A");
            await Wait(initialDelivery, "Event Hubs delivery through 100 on A");
            var firstA = Assert.Single(state.Epochs);
            var stableId = firstA.Address.GrainId;
            Assert.Equal("stream.pulling-agent", stableId.Type.ToString());
            Assert.Equal("20", firstA.LoadedOffset);
            Assert.Equal(new long[] { 20, 100 }, firstA.ReadOffsets.ToArray());
            Assert.Equal(new long[] { 20, 100 }, state.Delivered.ToArray());
            Assert.Equal("20", state.Persisted.Checkpoint);
            var factoryA = state.Factories[siloA];
            var queueId = Assert.Single(factoryA.GetStreamQueueMapper().GetAllQueues());
            var retainedReceiverA = Assert.IsType<EventHubAdapterReceiver>(factoryA.CreateReceiver(queueId));
            Assert.Same(retainedReceiverA, factoryA.CreateQueueCache(queueId));
            Assert.Single(TimersCreated(events, firstA));
            await Wait(cluster.Silos[1].ServiceProvider.GetRequiredKeyedService<IControllable>(EventHubMigrationState.ProviderName)
                .ExecuteCommand((int)PersistentStreamProviderCommand.StartAgents, null), "starting Event Hubs provider on B");

            var firstB = await Migrate(firstA, siloB, relayB, checkpoint: "100", nextAvailable: 101);
            var secondA = await Migrate(firstB, siloA, relayA, checkpoint: "101", nextAvailable: 102);

            Assert.Equal(3, state.Epochs.Count);
            Assert.Equal(stableId, secondA.Address.GrainId);
            Assert.Equal(siloA, secondA.Address.SiloAddress);
            Assert.NotEqual(firstA.Address.ActivationId, secondA.Address.ActivationId);
            Assert.Same(retainedReceiverA, factoryA.CreateReceiver(queueId));
            Assert.Same(retainedReceiverA, factoryA.CreateQueueCache(queueId));
            Assert.NotSame(firstA.Cache, secondA.Cache);
            Assert.NotSame(firstA.Checkpointer, secondA.Checkpointer);
            Assert.Equal(new long[] { 20, 100, 101, 102 }, state.Delivered.ToArray());
            Assert.Equal(1, state.ActiveSources);
            Assert.Equal(1, state.MaximumActiveSources);
            Assert.Equal(1, firstA.CloseCount);
            Assert.Equal(1, firstB.CloseCount);
            Assert.Equal(0, secondA.CloseCount);
            Assert.Equal(1, firstA.CacheDisposeCount);
            Assert.Equal(1, firstB.CacheDisposeCount);
            Assert.Equal(0, secondA.CacheDisposeCount);
            Assert.All(state.Epochs, epoch =>
            {
                Assert.IsType<EventHubQueueCache>(epoch.Cache);
                Assert.IsType<StreamQueueCheckpointer>(epoch.Checkpointer);
                Assert.Equal(1, epoch.LoadCount);
                Assert.Equal(0, epoch.ReadAfterCloseCount);
                Assert.Single(TimersCreated(events, epoch));
            });
            var disposedTimers = events.GetEvents(nameof(GrainTimerEvents.Disposed))
                .Select(evt => evt.Payload).OfType<GrainTimerEvents.Disposed>()
                .Where(evt => evt.GrainContext.GrainId == stableId).DistinctBy(evt => evt.Timer).ToArray();
            Assert.Equal(2, disposedTimers.Length);
            Assert.Contains(disposedTimers, evt => evt.GrainContext.ActivationId == firstA.Address.ActivationId);
            Assert.Contains(disposedTimers, evt => evt.GrainContext.ActivationId == firstB.Address.ActivationId);
            Assert.DoesNotContain(disposedTimers, evt => evt.GrainContext.ActivationId == secondA.Address.ActivationId);

            async Task<EventHubReceiverEpoch> Migrate(
                EventHubReceiverEpoch source,
                SiloAddress destinationSilo,
                IEventHubMigrationRelay relay,
                string checkpoint,
                long nextAvailable)
            {
                var previousEpochCount = state.Epochs.Count;
                var previousLoads = state.LoadsOn(destinationSilo);
                var previousReads = state.ReadsOn(destinationSilo);
                var previousDurable = state.Persisted.Checkpoint;
                var gate = state.ArmSave(source);
                var deactivated = events.WaitForEventAsync(
                    nameof(GrainLifecycleEvents.Deactivated),
                    evt => evt.Payload is GrainLifecycleEvents.Deactivated value && value.GrainContext.Address.Equals(source.Address),
                    PhaseTimeout, TestContext.Current.CancellationToken);
                var destinationDrained = WaitForDrain(events, state, destinationSilo, previousEpochCount);
                Assert.True(await Wait(cluster.Client.GetGrain<IEventHubMigrationProbe>(stableId)
                    .Rebalance(source.Address, destinationSilo), "guarded Event Hubs rebalance"));
                await Wait(gate.SaveEntered.Task, $"store write {checkpoint} from {source.Address}");
                await Wait(source.FlushEntered.Task, $"real EventHubAdapterReceiver final flush for {source.Address}");
                Assert.Equal(checkpoint, source.LastUpdate);
                Assert.Equal(previousDurable, state.Persisted.Checkpoint);
                Assert.False(deactivated.IsCompleted);
                Assert.Equal(0, source.CloseCount);
                Assert.Equal(0, source.CacheDisposeCount);
                var sourceReads = source.ReadCount;
                var probes = relay.Probe(stableId);
                await Wait(gate.ProbesIssued.Task, $"stable-grain calls through {destinationSilo}");
                Assert.False(probes.IsCompleted);
                Assert.Equal(previousLoads, state.LoadsOn(destinationSilo));
                Assert.Equal(previousReads, state.ReadsOn(destinationSilo));
                Assert.Equal(previousEpochCount, state.Epochs.Count);
                Assert.Equal(sourceReads, source.ReadCount);

                state.AvailableThrough = nextAvailable;
                gate.Release.TrySetResult();
                await Wait(deactivated, $"Event Hubs source deactivation after persist {checkpoint}");
                var addresses = await Wait(probes, "stable-grain calls completing on destination");
                await Wait(destinationDrained, $"Event Hubs destination delivery through {nextAvailable}");
                var destination = Assert.Single(state.Epochs, epoch => epoch.Ordinal == previousEpochCount + 1);
                Assert.Equal(checkpoint, destination.LoadedOffset);
                Assert.Equal(checkpoint, destination.SourceStartOffset);
                Assert.Equal(new[] { long.Parse(checkpoint, CultureInfo.InvariantCulture), nextAvailable }, destination.ReadOffsets.ToArray());
                Assert.Equal(checkpoint, state.Persisted.Checkpoint);
                Assert.Equal(1, source.FlushCount);
                Assert.Equal(1, source.CloseCount);
                Assert.Equal(1, source.CacheDisposeCount);
                Assert.All(addresses, address =>
                {
                    Assert.Equal(stableId, address.GrainId);
                    Assert.Equal(destinationSilo, address.SiloAddress);
                    Assert.NotEqual(source.Address.ActivationId, address.ActivationId);
                    Assert.Equal(destination.Address.ActivationId, address.ActivationId);
                });
                return destination;
            }
        }
        finally
        {
            state.ReleaseSave();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiverShutdown_FailedOrCanceledFlushReleasesResourcesAndReinitializes(bool cancelStoreWrite)
    {
        var state = new EventHubMigrationState();
        await using var cluster = CreateCluster(state);
        try
        {
            await Deploy(cluster);
            var grain = cluster.Client.GetGrain<IEventHubReceiverLifecycleGrain>(Guid.NewGuid());
            var original = await Wait(grain.Initialize(), "initial real Event Hubs receiver initialization");
            Assert.Equal(new long[] { 20, 100 }, await Wait(grain.Read(), "initial real Event Hubs cache read"));
            var first = Assert.Single(state.Epochs);
            var gate = state.ArmSave(first, cancelStoreWrite ? EventHubWriteFailure.Canceled : EventHubWriteFailure.Failed);
            await Wait(grain.Acknowledge("100"), "acknowledged Event Hubs offset 100");
            var shutdown = grain.Shutdown();
            await Wait(gate.SaveEntered.Task, "failing final store write");
            await Wait(first.FlushEntered.Task, "real receiver awaiting failed flush");
            Assert.Equal("20", state.Persisted.Checkpoint);
            gate.Release.TrySetResult();
            if (cancelStoreWrite)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(shutdown, "canceled receiver flush"));
            }
            else
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Wait(shutdown, "failed receiver flush"));
                Assert.Equal(EventHubMigrationState.StoreFailureMessage, exception.Message);
            }

            Assert.Equal(2, gate.WriteAttempts);
            Assert.Equal("20", state.Persisted.Checkpoint);
            Assert.Equal(1, first.CloseCount);
            Assert.Equal(1, first.CacheDisposeCount);
            Assert.Equal(0, state.ActiveSources);
            state.ClearSave();
            Assert.Equal(original, await Wait(grain.Initialize(), "receiver reinitialization after failed flush"));
            var second = Assert.Single(state.Epochs, epoch => epoch.Ordinal == 2);
            Assert.Equal("20", second.LoadedOffset);
            Assert.NotSame(first.Cache, second.Cache);
            Assert.NotSame(first.Checkpointer, second.Checkpointer);
            Assert.Equal(new long[] { 20, 100 }, await Wait(grain.Read(), "fresh cache replay after failed flush"));
            await Wait(grain.Acknowledge("100"), "recovered receiver acknowledgement");
            await Wait(grain.Shutdown(), "recovered receiver final flush");
            Assert.Equal("100", state.Persisted.Checkpoint);
            Assert.Equal(1, second.CloseCount);
            Assert.Equal(1, second.CacheDisposeCount);
            Assert.Equal(0, state.ActiveSources);
            Assert.Equal(1, state.MaximumActiveSources);
        }
        finally
        {
            state.ReleaseSave();
        }
    }

    private static async Task RunGracefulShutdown(bool failFlush)
    {
        var clock = new FakeTimeProvider();
        var state = new EventHubMigrationState { ObserveMigration = true, GrainClock = clock };
        await using var cluster = CreateCluster(state);
        using var events = new DiagnosticEventCollector(StreamingEvents.ListenerName, GrainLifecycleEvents.ListenerName);
        GrainAddress? observedAddress = null;
        var callbackCompleted = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ActivitySources.LifecycleActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (observedAddress is { } address && activity.OperationName == ActivityNames.OnDeactivate
                    && Equals(activity.GetTagItem(ActivityTagKeys.GrainId), address.GrainId.ToString())
                    && Equals(activity.GetTagItem(ActivityTagKeys.ActivationId), address.ActivationId.ToString()))
                {
                    callbackCompleted.TrySetResult(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(activities);
        Task stopping = Task.CompletedTask;
        try
        {
            await Deploy(cluster);
            var sourceSilo = cluster.Silos[0];
            var survivor = cluster.Silos[1];
            var consumer = cluster.Client.GetGrain<IEventHubMigrationConsumer>(Guid.NewGuid());
            RequestContext.Set(IPlacementDirector.PlacementHintKey, survivor.SiloAddress);
            try
            {
                Assert.Equal(survivor.SiloAddress, await Wait(consumer.Subscribe(), "surviving Event Hubs consumer subscription"));
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            var rendezvous = Assert.Single(events.GetEvents(nameof(GrainLifecycleEvents.Activated))
                .Select(evt => evt.Payload).OfType<GrainLifecycleEvents.Activated>(),
                evt => evt.GrainContext.GrainInstance?.GetType().Name == "PubSubRendezvousGrain");
            Assert.Equal(survivor.SiloAddress, rendezvous.GrainContext.Address.SiloAddress);
            var initialDelivery = WaitForDrain(events, state, sourceSilo.SiloAddress);
            await Wait(sourceSilo.ServiceProvider.GetRequiredKeyedService<IControllable>(EventHubMigrationState.ProviderName)
                .ExecuteCommand((int)PersistentStreamProviderCommand.StartAgents, null), "starting shutdown-test source provider");
            var coordinatorId = GrainId.Create("stream.pulling-agent-coordinator", EventHubMigrationState.ProviderName);
            clock.Advance(TimeSpan.Zero);
            await Wait(cluster.Client.GetGrain<IEventHubMigrationProbe>(coordinatorId).GetAddress(), "initial coordinator round mailbox barrier");
            clock.Advance(TimeSpan.FromSeconds(1));
            await Wait(initialDelivery, "source Event Hubs acknowledgement through 100");
            var source = Assert.Single(state.Epochs);
            observedAddress = source.Address;
            Assert.Equal("20", state.Persisted.Checkpoint);
            Assert.Equal(1, await Wait(consumer.GetProducerCount(), "initial real rendezvous producer registration"));
            await Wait(survivor.ServiceProvider.GetRequiredKeyedService<IControllable>(EventHubMigrationState.ProviderName)
                .ExecuteCommand((int)PersistentStreamProviderCommand.StartAgents, null), "starting surviving Event Hubs provider");

            // Quiesce membership-driven evacuation and hold the local heartbeat clocks stationary.
            // The production agent's ShuttingDown callback is the sole migration initiator in this scenario.
            await Wait(cluster.DeactivateAsync(coordinatorId), "quiescing coordinator before catalog shutdown");
            Assert.False(cluster.TryGetGrainContext(coordinatorId, out _));
            var gate = state.ArmSave(source, failFlush ? EventHubWriteFailure.Failed : EventHubWriteFailure.None);
            var deactivating = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Deactivating),
                evt => evt.Payload is GrainLifecycleEvents.Deactivating value && value.GrainContext.Address.Equals(source.Address),
                PhaseTimeout, TestContext.Current.CancellationToken);
            var deactivated = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Deactivated),
                evt => evt.Payload is GrainLifecycleEvents.Deactivated value && value.GrainContext.Address.Equals(source.Address),
                PhaseTimeout, TestContext.Current.CancellationToken);
            var successorDelivered = WaitForDrain(events, state, survivor.SiloAddress, afterEpoch: 1);
            stopping = cluster.StopSiloAsync(sourceSilo, TestContext.Current.CancellationToken);
            await Wait(gate.SaveEntered.Task, "runtime-deactivation Event Hubs checkpoint write");
            await Wait(source.FlushEntered.Task, "Event Hubs receiver flush from grain deactivation");
            var lifecycle = Assert.IsType<GrainLifecycleEvents.Deactivating>((await Wait(deactivating, "source grain deactivation entry")).Payload);
            Assert.Equal(DeactivationReasonCode.ShuttingDown, lifecycle.Reason.ReasonCode);
            var flushActivity = Assert.IsType<Activity>(source.FlushActivity);
            Assert.Equal(ActivityNames.OnDeactivate, flushActivity.OperationName);
            Assert.Equal(source.Address.ActivationId.ToString(), flushActivity.GetTagItem(ActivityTagKeys.ActivationId));
            Assert.False(deactivated.IsCompleted);
            Assert.False(callbackCompleted.Task.IsCompleted);
            Assert.Equal("100", source.LastUpdate);
            Assert.Equal("20", state.Persisted.Checkpoint);
            Assert.Equal(0, source.CloseCount);
            Assert.Equal(0, source.CacheDisposeCount);
            var sourceReads = source.ReadCount;
            Assert.Equal(1, await Wait(consumer.GetProducerCount(), "retained producer during blocked runtime deactivation"));
            Assert.Equal(0, state.LoadsOn(survivor.SiloAddress));
            Assert.Equal(0, state.ReadsOn(survivor.SiloAddress));
            Assert.Single(state.Epochs);
            Assert.Equal(sourceReads, source.ReadCount);
            Assert.False(cluster.TryGetGrainContext(coordinatorId, out _));

            state.AvailableThrough = 101;
            gate.Release.TrySetResult();
            await Wait(deactivated, "source grain completing runtime deactivation");
            var outcome = await Wait(callbackCompleted.Task, "observable runtime OnDeactivateAsync outcome");
            await Wait(stopping, "graceful source silo shutdown");
            Assert.Equal(1, source.FlushCount);
            Assert.Equal(1, source.CloseCount);
            Assert.Equal(1, source.CacheDisposeCount);
            if (failFlush)
            {
                Assert.Equal(ActivityStatusCode.Error, outcome.Status);
                Assert.Equal(typeof(InvalidOperationException).FullName, outcome.GetTagItem(ActivityTagKeys.ExceptionType));
                Assert.Equal(EventHubMigrationState.StoreFailureMessage, outcome.GetTagItem(ActivityTagKeys.ExceptionMessage));
                Assert.Equal(2, gate.WriteAttempts);
                Assert.Equal(0, source.DehydrationCount);
                Assert.Equal("20", state.Persisted.Checkpoint);
            }
            else
            {
                Assert.Equal(ActivityStatusCode.Unset, outcome.Status);
                Assert.Equal(1, source.DehydrationCount);
                Assert.Equal("100", state.Persisted.Checkpoint);
            }

            await Wait(cluster.WaitForLivenessToStabilizeAsync(), "surviving silo membership");
            // After failure this ordinary lookup may cold-activate the grain; source dehydration
            // above distinguishes that recovery from a cooperative transfer of the failed activation.
            var address = await Wait(cluster.Client.GetGrain<IEventHubMigrationProbe>(source.Address.GrainId).GetAddress(),
                "successor address after source shutdown");
            clock.Advance(TimeSpan.FromSeconds(1));
            await Wait(successorDelivered, "successor Event Hubs delivery through 101");
            var successor = Assert.Single(state.Epochs, epoch => epoch.Ordinal == 2);
            var checkpoint = failFlush ? "20" : "100";
            Assert.Equal(checkpoint, successor.LoadedOffset);
            Assert.Equal(checkpoint, successor.SourceStartOffset);
            Assert.Equal(failFlush ? new long[] { 20, 100, 101 } : [100, 101], successor.ReadOffsets.ToArray());
            Assert.Equal(source.Address.GrainId, address.GrainId);
            Assert.Equal(survivor.SiloAddress, address.SiloAddress);
            Assert.NotEqual(source.Address.ActivationId, address.ActivationId);
            Assert.Equal(successor.Address, address);
            Assert.Equal(1, successor.LoadCount);
            Assert.Equal(new long[] { 20, 100, 101 }, state.Delivered.ToArray());
            Assert.Equal(1, state.MaximumActiveSources);
            Assert.Equal(1, await Wait(consumer.GetProducerCount(), "stable producer after shutdown recovery"));
        }
        finally
        {
            state.ReleaseSave();
            await Wait(stopping, "source shutdown cleanup");
        }
    }

    private static InProcessTestCluster CreateCluster(EventHubMigrationState state)
    {
        var builder = new InProcessTestClusterBuilder(2);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003
        builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton(state);
            if (state.GrainClock is { } clock)
            {
                silo.Services.AddSingleton<TimeProvider>(clock);
                silo.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);
            }

            silo.Services.AddKeyedSingleton<IStreamQueueCheckpointerFactory>(
                EventHubMigrationState.ProviderName, (services, _) => new EventHubMigrationCheckpointerFactory(
                    state, services.GetRequiredService<IGrainContextAccessor>()));
            silo.AddMemoryGrainStorage("PubSubStore");
            silo.AddGrainExtension<IEventHubMigrationProbe, EventHubMigrationProbe>();
            silo.AddPersistentStreams(EventHubMigrationState.ProviderName, ControlledEventHubAdapterFactory.Create,
                configurator =>
                {
                    configurator.ConfigurePullingAgent(options => options.Configure(value =>
                        value.HostingMode = Orleans.Configuration.StreamPullingAgentHostingMode.Grain));
                    configurator.ConfigureLifecycle(options => options.Configure(value =>
                        value.StartupState = Orleans.Configuration.StreamLifecycleOptions.RunState.AgentsStopped));
                    configurator.ConfigureStreamPubSub(StreamPubSubType.ExplicitGrainBasedOnly);
                    configurator.ConfigurePartitionBalancing((_, _) =>
                        throw new InvalidOperationException("Grain hosting must not construct a queue balancer."));
                });
        });
        return builder.Build();
    }

    private static async Task Deploy(InProcessTestCluster cluster)
    {
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        await Wait(cluster.WaitForLivenessToStabilizeAsync(), "Event Hubs test cluster liveness");
        await Wait(cluster.WaitForClusterManifestToStabilizeAsync(), "Event Hubs provider manifest");
    }

    private static async Task Place(IEventHubMigrationRelay grain, SiloAddress silo)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, silo);
        try
        {
            Assert.Equal(silo, await Wait(grain.GetHost(), "migration relay placement"));
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }
    }

    private static Task<DiagnosticEvent> WaitForDrain(
        DiagnosticEventCollector events, EventHubMigrationState state, SiloAddress silo, int afterEpoch = 0)
        => events.WaitForEventAsync(nameof(StreamingEvents.ConsumerCursorDrained),
            evt => evt.Payload is StreamingEvents.ConsumerCursorDrained value
                && value.StreamId == state.StreamId && value.SiloAddress == silo
                && state.Epochs.Any(epoch => epoch.Ordinal > afterEpoch && epoch.Address.SiloAddress == silo
                    && evt.Timestamp >= epoch.CreatedAt),
            PhaseTimeout, TestContext.Current.CancellationToken);

    private static IEnumerable<GrainTimerEvents.Created> TimersCreated(DiagnosticEventCollector events, EventHubReceiverEpoch epoch)
        => events.GetEvents(nameof(GrainTimerEvents.Created)).Select(evt => evt.Payload).OfType<GrainTimerEvents.Created>()
            .Where(evt => evt.GrainContext.ActivationId == epoch.Address.ActivationId);

    private static async Task Wait(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out awaiting {phase}.", exception);
        }
    }

    private static async Task<T> Wait<T>(Task<T> task, string phase)
    {
        await Wait((Task)task, phase);
        return await task;
    }
}

public interface IEventHubMigrationProbe : IGrainExtension
{
    Task<GrainAddress> GetAddress();
    Task<bool> Rebalance(GrainAddress expectedAddress, SiloAddress destination);
}

public sealed class EventHubMigrationProbe(IGrainContextAccessor context) : IEventHubMigrationProbe
{
    public Task<GrainAddress> GetAddress() => Task.FromResult(context.GrainContext.Address);

    public Task<bool> Rebalance(GrainAddress expectedAddress, SiloAddress destination)
    {
        // This provider test assembly has no friend access to the internal hosting interface.
        var host = context.GrainContext.GrainInstance!;
        var method = host.GetType().GetMethod(nameof(Rebalance), [typeof(GrainAddress), typeof(SiloAddress), typeof(CancellationToken)]);
        Assert.NotNull(method);
        return (Task<bool>)method.Invoke(host, [expectedAddress, destination, CancellationToken.None])!;
    }
}

public interface IEventHubMigrationRelay : IGrainWithGuidKey
{
    Task<SiloAddress> GetHost();
    Task<GrainAddress[]> Probe(GrainId grainId);
}

public sealed class EventHubMigrationRelay(EventHubMigrationState state) : Grain, IEventHubMigrationRelay
{
    public Task<SiloAddress> GetHost() => Task.FromResult(GrainContext.Address.SiloAddress!);

    public async Task<GrainAddress[]> Probe(GrainId grainId)
    {
        var grain = GrainFactory.GetGrain<IEventHubMigrationProbe>(grainId);
        Task<GrainAddress>[] calls = [grain.GetAddress(), grain.GetAddress(), grain.GetAddress()];
        state.SaveGate!.ProbesIssued.TrySetResult();
        return await Task.WhenAll(calls);
    }
}

public interface IEventHubMigrationConsumer : IGrainWithGuidKey
{
    Task<SiloAddress> Subscribe();
    Task<int> GetProducerCount();
}

public sealed class EventHubMigrationConsumer(EventHubMigrationState state) : Grain, IEventHubMigrationConsumer, IAsyncObserver<long>
{
    public async Task<SiloAddress> Subscribe()
    {
        await this.GetStreamProvider(EventHubMigrationState.ProviderName).GetStream<long>(state.StreamId).SubscribeAsync(this);
        return GrainContext.Address.SiloAddress!;
    }

    public Task<int> GetProducerCount()
    {
        // Query the real pub/sub runtime without adding friend access for this provider test assembly.
        var runtimeType = typeof(IStreamPubSub).Assembly.GetType("Orleans.Streams.IStreamProviderRuntime", throwOnError: true)!;
        var runtime = ServiceProvider.GetRequiredService(runtimeType);
        var pubSub = (IStreamPubSub)runtimeType.GetMethod("PubSub")!.Invoke(runtime, [StreamPubSubType.ExplicitGrainBasedOnly])!;
        return pubSub.ProducerCount(new QualifiedStreamId(EventHubMigrationState.ProviderName, state.StreamId), CancellationToken.None);
    }

    public Task OnNextAsync(long item, StreamSequenceToken? token = null)
    {
        Assert.IsType<EventHubSequenceTokenV2>(token);
        state.Delivered.Enqueue(item);
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception exception) => Task.FromException(exception);
}

public interface IEventHubReceiverLifecycleGrain : IGrainWithGuidKey
{
    Task<GrainAddress> Initialize();
    Task<long[]> Read();
    Task Acknowledge(string offset);
    Task Shutdown();
}

public sealed class EventHubReceiverLifecycleGrain(EventHubMigrationState state) : Grain, IEventHubReceiverLifecycleGrain
{
    private IQueueAdapterReceiver _receiver = null!;
    private IQueueCache _cache = null!;
    private bool _running;

    public async Task<GrainAddress> Initialize()
    {
        var factory = (EventHubAdapterFactory)ServiceProvider.GetRequiredKeyedService<IQueueAdapterFactory>(EventHubMigrationState.ProviderName);
        var queue = Assert.Single(factory.GetStreamQueueMapper().GetAllQueues());
        var receiver = factory.CreateReceiver(queue);
        if (_receiver is not null)
        {
            Assert.Same(_receiver, receiver);
        }

        _receiver = receiver;
        _cache = factory.CreateQueueCache(queue);
        Assert.Same(_receiver, _cache);
        await _receiver.Initialize(TimeSpan.FromSeconds(30));
        _running = true;
        return GrainContext.Address;
    }

    public async Task<long[]> Read()
    {
        await _receiver.GetQueueMessagesAsync(10, CancellationToken.None);
        var result = _cache.TryGetCacheCursor(state.StreamId, new EventHubSequenceTokenV2("20", 20, 0));
        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        using var cursor = result.Cursor!;
        var values = new List<long>();
        while (cursor.MoveNextWithResult().Kind == QueueCacheCursorMoveResultKind.Success)
        {
            var batch = cursor.GetCurrent(out var exception);
            Assert.Null(exception);
            values.AddRange(batch!.GetEvents<long>().Select(item => item.Item1));
        }

        return values.ToArray();
    }

    public Task Acknowledge(string offset)
    {
        _cache.UpdateDeliveryProgress(new EventHubSequenceTokenV2(offset, long.Parse(offset, CultureInfo.InvariantCulture), 0), DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public async Task Shutdown()
    {
        try
        {
            await _receiver.Shutdown(TimeSpan.FromSeconds(30));
        }
        finally
        {
            _running = false;
        }
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        => _running ? Shutdown() : Task.CompletedTask;
}

public sealed class EventHubMigrationState
{
    internal const string ProviderName = "event-hubs-grain-migration";
    internal const string StoreFailureMessage = "Controlled Event Hubs checkpoint store write failed.";
    private readonly object _lock = new();
    private StreamCheckpointStoreState _persisted = new("20", "0");
    private int _version;
    private int _activeSources;
    private int _maximumActiveSources;
    private long _availableThrough = 100;
    internal bool ObserveMigration { get; init; }
    internal FakeTimeProvider? GrainClock { get; init; }
    internal StreamId StreamId { get; } = StreamId.Create("event-hubs-migration", Guid.NewGuid());
    internal ConcurrentQueue<EventHubReceiverEpoch> Epochs { get; } = new();
    internal ConcurrentQueue<long> Delivered { get; } = new();
    internal ConcurrentDictionary<SiloAddress, ControlledEventHubAdapterFactory> Factories { get; } = new();
    internal EventHubSaveGate? SaveGate { get; private set; }
    internal int ActiveSources => Volatile.Read(ref _activeSources);
    internal int MaximumActiveSources => Volatile.Read(ref _maximumActiveSources);
    internal long AvailableThrough { get => Interlocked.Read(ref _availableThrough); set => Interlocked.Exchange(ref _availableThrough, value); }
    internal StreamCheckpointStoreState Persisted { get { lock (_lock) { return _persisted; } } }
    internal int LoadsOn(SiloAddress silo) => Epochs.Where(epoch => epoch.Address.SiloAddress == silo).Sum(epoch => epoch.LoadCount);
    internal int ReadsOn(SiloAddress silo) => Epochs.Where(epoch => epoch.Address.SiloAddress == silo).Sum(epoch => epoch.ReadCount);

    internal EventHubSaveGate ArmSave(EventHubReceiverEpoch epoch, EventHubWriteFailure failure = EventHubWriteFailure.None)
        => SaveGate = new EventHubSaveGate(epoch, failure);
    internal void ReleaseSave() => SaveGate?.Release.TrySetResult();
    internal void ClearSave() => SaveGate = null;

    internal void OpenSource()
    {
        lock (_lock)
        {
            _activeSources++;
            _maximumActiveSources = Math.Max(_maximumActiveSources, _activeSources);
        }
    }

    internal void CloseSource()
    {
        lock (_lock)
        {
            _activeSources--;
        }
    }

    internal async ValueTask<StreamCheckpointStoreState> Update(
        EventHubReceiverEpoch epoch, string checkpoint, string expectedVersion, CancellationToken cancellationToken)
    {
        if (SaveGate is { } gate && ReferenceEquals(gate.Epoch, epoch))
        {
            Interlocked.Increment(ref gate.WriteAttempts);
            gate.SaveEntered.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
            if (gate.Failure == EventHubWriteFailure.Failed)
            {
                throw new InvalidOperationException(StoreFailureMessage);
            }

            if (gate.Failure == EventHubWriteFailure.Canceled)
            {
                throw new OperationCanceledException("Controlled checkpoint store cancellation.", new CancellationToken(canceled: true));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_persisted.Version == expectedVersion)
            {
                _persisted = new(checkpoint, (++_version).ToString(CultureInfo.InvariantCulture));
            }

            return _persisted;
        }
    }
}

internal enum EventHubWriteFailure { None, Failed, Canceled }

internal sealed class EventHubSaveGate(EventHubReceiverEpoch epoch, EventHubWriteFailure failure)
{
    internal EventHubReceiverEpoch Epoch { get; } = epoch;
    internal EventHubWriteFailure Failure { get; } = failure;
    internal int WriteAttempts;
    internal TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ProbesIssued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class EventHubReceiverEpoch(GrainAddress address, int ordinal)
{
    internal GrainAddress Address { get; } = address;
    internal int Ordinal { get; } = ordinal;
    internal DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    internal IEventHubQueueCache Cache { get; set; } = null!;
    internal StreamQueueCheckpointer Checkpointer { get; set; } = null!;
    internal string LoadedOffset { get; set; } = "";
    internal string SourceStartOffset { get; set; } = "";
    internal string? LastUpdate { get; set; }
    internal int LoadCount;
    internal int ReadCount;
    internal int ReadAfterCloseCount;
    internal int CloseCount;
    internal int CacheDisposeCount;
    internal int FlushCount;
    internal int DehydrationCount;
    internal Activity? FlushActivity;
    internal ConcurrentQueue<long> ReadOffsets { get; } = new();
    internal TaskCompletionSource FlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class EventHubMigrationCheckpointerFactory(EventHubMigrationState state, IGrainContextAccessor context) : IStreamQueueCheckpointerFactory
{
    public Task<IStreamQueueCheckpointer<string>> Create(string partition) => Create(partition, CancellationToken.None);

    public Task<IStreamQueueCheckpointer<string>> Create(string partition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal("0", partition);
        var epoch = new EventHubReceiverEpoch(context.GrainContext.Address, state.Epochs.Count + 1);
        var checkpointer = new StreamQueueCheckpointer(new Store(state, epoch), new StreamQueueCheckpointerOptions
        {
            PersistInterval = TimeSpan.FromHours(1),
            CheckpointComparer = StreamCheckpointComparers.Numeric,
        });
        epoch.Checkpointer = checkpointer;
        if (state.ObserveMigration)
        {
            context.GrainContext.ObservableLifecycle.AddMigrationParticipant(new MigrationObservation(epoch));
        }

        state.Epochs.Enqueue(epoch);
        return Task.FromResult<IStreamQueueCheckpointer<string>>(new ObservedEventHubCheckpointer(epoch));
    }

    private sealed class MigrationObservation(EventHubReceiverEpoch epoch) : IGrainMigrationParticipant
    {
        public void OnDehydrate(IDehydrationContext context) => Interlocked.Increment(ref epoch.DehydrationCount);
        public void OnRehydrate(IRehydrationContext context) { }
    }

    private sealed class Store(EventHubMigrationState state, EventHubReceiverEpoch epoch) : IStreamCheckpointStore
    {
        public ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var persisted = state.Persisted;
            epoch.LoadedOffset = persisted.Checkpoint;
            Interlocked.Increment(ref epoch.LoadCount);
            return new(persisted);
        }

        public ValueTask<StreamCheckpointStoreState> Update(string checkpoint, string expectedVersion, CancellationToken cancellationToken)
            => state.Update(epoch, checkpoint, expectedVersion, cancellationToken);
    }
}

internal sealed class ObservedEventHubCheckpointer(EventHubReceiverEpoch epoch) : IStreamQueueCheckpointer<string>
{
    internal EventHubReceiverEpoch Epoch => epoch;
    public bool CheckpointExists => epoch.Checkpointer.CheckpointExists;
    public Task<string> Load() => Load(CancellationToken.None);
    public Task<string> Load(CancellationToken cancellationToken) => epoch.Checkpointer.Load(cancellationToken);
    public void Update(string offset, DateTime utcNow) => Update(offset, utcNow, CancellationToken.None);
    public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
    {
        epoch.LastUpdate = offset;
        epoch.Checkpointer.Update(offset, utcNow, cancellationToken);
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref epoch.FlushCount);
        epoch.FlushActivity = Activity.Current;
        epoch.FlushEntered.TrySetResult();
        return epoch.Checkpointer.FlushAsync(cancellationToken);
    }
}

internal sealed class ControlledEventHubAdapterFactory : EventHubAdapterFactory
{
    private readonly SiloAddress _silo;

    private ControlledEventHubAdapterFactory(IServiceProvider services, string name, EventHubMigrationState state)
        : base(name, CreateOptions(), new Orleans.Configuration.EventHubReceiverOptions(),
            new Orleans.Configuration.EventHubStreamCachePressureOptions(),
            new Orleans.Configuration.StreamCacheEvictionOptions
            {
                DataMinTimeInCache = TimeSpan.FromHours(1),
                DataMaxAgeInCache = TimeSpan.FromHours(1),
                MetadataMinTimeInCache = TimeSpan.FromHours(1),
            },
            new Orleans.Configuration.StreamStatisticOptions(),
            new EventHubDataAdapter(services.GetRequiredService<Serializer>()),
            services, services.GetRequiredService<ILoggerFactory>(), services.GetRequiredService<IEnvironmentStatisticsProvider>())
    {
        _silo = services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        Direction = StreamProviderDirection.ReadOnly;
        EventHubReceiverFactory = (settings, offset, _) =>
        {
            Assert.Equal("0", settings.Partition);
            var epoch = state.Epochs.Last(value => value.Address.SiloAddress == _silo);
            epoch.SourceStartOffset = offset;
            return new ControlledEventHubSource(state, epoch, dataAdapter, offset);
        };
    }

    internal static new IQueueAdapterFactory Create(IServiceProvider services, string name)
    {
        var state = services.GetRequiredService<EventHubMigrationState>();
        var factory = new ControlledEventHubAdapterFactory(services, name, state);
        factory.Init();
        Assert.True(state.Factories.TryAdd(factory._silo, factory));
        return factory;
    }

    protected override void InitEventHubClient() { }
    protected override Task<string[]> GetPartitionIdsAsync() => Task.FromResult(new[] { "0" });

    protected override IEventHubQueueCacheFactory CreateCacheFactory(Orleans.Configuration.EventHubStreamCachePressureOptions options)
        => new ObservedCacheFactory(base.CreateCacheFactory(options));

    private static Orleans.Configuration.EventHubOptions CreateOptions()
    {
        var options = new Orleans.Configuration.EventHubOptions();
        options.ConfigureEventHubConnection(_ => throw new InvalidOperationException("The controlled Event Hubs source owns all reads."),
            "migration-test-hub", "migration-test-consumers");
        return options;
    }

    private sealed class ObservedCacheFactory(IEventHubQueueCacheFactory inner) : IEventHubQueueCacheFactory
    {
        public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, ILoggerFactory loggerFactory)
        {
            var epoch = Assert.IsType<ObservedEventHubCheckpointer>(checkpointer).Epoch;
            epoch.Cache = inner.CreateCache(partition, checkpointer, loggerFactory);
            return new ObservedEventHubCache(epoch);
        }
    }
}

internal sealed class ControlledEventHubSource : IEventHubReceiver
{
    private static readonly long[] Offsets = [20, 100, 101, 102];
    private readonly EventHubMigrationState _state;
    private readonly EventHubReceiverEpoch _epoch;
    private readonly IEventHubDataAdapter _adapter;
    private int _nextIndex;
    private bool _closed;

    internal ControlledEventHubSource(EventHubMigrationState state, EventHubReceiverEpoch epoch, IEventHubDataAdapter adapter, string offset)
    {
        _state = state;
        _epoch = epoch;
        _adapter = adapter;
        var start = long.Parse(offset, CultureInfo.InvariantCulture);
        while (_nextIndex < Offsets.Length && Offsets[_nextIndex] < start)
        {
            _nextIndex++;
        }

        state.OpenSource();
    }

    public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        => ReceiveAsync(maxCount, waitTime, CancellationToken.None);

    public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed)
        {
            Interlocked.Increment(ref _epoch.ReadAfterCloseCount);
            throw new InvalidOperationException("Read from a closed Event Hubs source.");
        }

        Interlocked.Increment(ref _epoch.ReadCount);
        var messages = new List<EventData>();
        while (_nextIndex < Offsets.Length && messages.Count < maxCount && Offsets[_nextIndex] <= _state.AvailableThrough)
        {
            var offset = Offsets[_nextIndex++];
            var serialized = _adapter.ToQueueMessage(_state.StreamId, new[] { offset }, null, null);
            messages.Add(EventHubsModelFactory.EventData(
                eventBody: serialized.EventBody,
                properties: serialized.Properties,
                partitionKey: _adapter.GetPartitionKey(_state.StreamId),
                sequenceNumber: offset,
                offsetString: offset.ToString(CultureInfo.InvariantCulture),
                enqueuedTime: DateTimeOffset.UtcNow));
            _epoch.ReadOffsets.Enqueue(offset);
        }

        return Task.FromResult<IEnumerable<EventData>>(messages);
    }

    public Task CloseAsync() => CloseAsync(CancellationToken.None);
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.False(_closed);
        _closed = true;
        Interlocked.Increment(ref _epoch.CloseCount);
        _state.CloseSource();
        return Task.CompletedTask;
    }
}

internal sealed class ObservedEventHubCache(EventHubReceiverEpoch epoch) : IEventHubQueueCache
{
    public int GetMaxAddCount() => epoch.Cache.GetMaxAddCount();
    public List<StreamPosition> Add(List<EventData> messages, DateTime dequeueTimeUtc) => epoch.Cache.Add(messages, dequeueTimeUtc);
    public QueueCacheCursorResult<object> TryGetCursor(StreamId streamId, StreamSequenceToken? token) => epoch.Cache.TryGetCursor(streamId, token);
    public QueueCacheCursorResult<object> TryGetCursorAtPosition(StreamId streamId, StreamSubscriptionStartPosition position)
        => epoch.Cache.TryGetCursorAtPosition(streamId, position);
    public QueueCacheCursorMoveResult TryGetNextMessageWithResult(object cursor, out IBatchContainer? message)
        => epoch.Cache.TryGetNextMessageWithResult(cursor, out message);
    public void Refresh(object cursor, StreamSequenceToken? token) => epoch.Cache.Refresh(cursor, token);
    public void AddCachePressureMonitor(ICachePressureMonitor monitor) => epoch.Cache.AddCachePressureMonitor(monitor);
    public void SignalPurge() => epoch.Cache.SignalPurge();
    public void Dispose()
    {
        Assert.Equal(1, Interlocked.Increment(ref epoch.CacheDisposeCount));
        epoch.Cache.Dispose();
    }

#pragma warning disable CS0618 // Forward the required legacy interface members to the real cache.
    public object GetCursor(StreamId streamId, StreamSequenceToken? token) => epoch.Cache.GetCursor(streamId, token);
    public bool TryGetNextMessage(object cursor, [NotNullWhen(true)] out IBatchContainer? message) => epoch.Cache.TryGetNextMessage(cursor, out message);
#pragma warning restore CS0618
}
