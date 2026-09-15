using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Streaming.Diagnostics;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using Orleans.Timers;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public sealed class PullingAgentMigrationTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("provider", "queue", 0u, 0u)]
    [InlineData("Provider:West/Hub", "Partition:0", 1u, uint.MaxValue)]
    [InlineData("provider", "", uint.MaxValue, 0u)]
    [InlineData("1:a", "2:bc", 0x01020304u, 0xFEDCBA98u)]
    [InlineData("PROVIDER", "Queue", uint.MaxValue, uint.MaxValue)]
    [InlineData("caf\u00E9:\u6C34", "partition:\U0001F30A", 17u, 42u)]
    public void PullingAgentId_RoundTripsExactProviderAndQueueIdentity(string providerName, string prefix, uint number, uint hash)
    {
        var queueId = QueueId.GetQueueId(prefix, number, hash);
        var grainId = PullingAgentId.Create(providerName, queueId);

        var parsed = PullingAgentId.Parse(grainId);

        Assert.Equal("stream.agent", grainId.Type.ToString());
        Assert.Equal(providerName, parsed.ProviderName);
        Assert.Equal(prefix, parsed.QueueId.GetStringNamePrefix());
        Assert.Equal(number, parsed.QueueId.GetNumericId());
        Assert.Equal(hash, parsed.QueueId.GetUniformHashCode());
        Assert.Equal(queueId, parsed.QueueId);
        Assert.Equal(grainId, PullingAgentId.Create(providerName, QueueId.GetQueueId(prefix, number, hash)));
    }

    [Fact]
    public void PullingAgentId_EncodesLengthPrefixedNamesAndFixedWidthNumbers()
    {
        var grainId = PullingAgentId.Create("ab:c", QueueId.GetQueueId("Q:", 0x12u, 0x34u));

        Assert.Equal("4:ab:c2:Q:0000001200000034", grainId.Key.ToString());
    }

    [Fact]
    public void PullingAgentId_DistinguishesEveryIdentityComponentAndNameBoundary()
    {
        (string Provider, string Prefix, uint Number, uint Hash)[] identities =
        [
            ("p", "q", 0, 0),
            ("P", "q", 0, 0),
            ("p", "Q", 0, 0),
            ("p", "q", 1, 0),
            ("p", "q", 0, 1),
            ("p", "q", uint.MaxValue, uint.MaxValue),
            ("ab", "c", 0, 0),
            ("a", "bc", 0, 0),
            ("a:b", "c", 0, 0),
            ("a", "b:c", 0, 0),
        ];
        var grainIds = identities.Select(identity => PullingAgentId.Create(
            identity.Provider,
            QueueId.GetQueueId(identity.Prefix, identity.Number, identity.Hash))).ToArray();

        Assert.Equal(identities.Length, grainIds.Distinct().Count());
    }

    [Fact]
    public Task MigrateOnIdle_WaitsForFinalCheckpointBeforeDestinationReads() => RunHandoff(stopSourceSilo: false);

    [Fact]
    public Task GracefulSiloShutdown_WaitsForFinalCheckpointBeforeDestinationReads() => RunHandoff(stopSourceSilo: true);

    [Fact]
    public Task MigrateOnIdle_CancelsPendingRegistrationAndPreservesCheckpoint() => RunInterruptedHandoff(blockRegistration: true);

    [Fact]
    public Task MigrateOnIdle_CancelsUnacknowledgedDeliveryAndPreservesCheckpoint() => RunInterruptedHandoff(blockRegistration: false);

    [Fact]
    public async Task GrainHostingMode_RebalancesProductionHostAfterFinalCheckpoint()
    {
        var state = new PullingAgentMigrationState();
        var builder = new InProcessTestClusterBuilder(2);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003
        builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton(state);
            silo.AddMemoryGrainStorage("PubSubStore");
            silo.AddPersistentStreams(
                PullingAgentMigrationState.ProviderName,
                (services, _) => new HostedMigrationAdapterFactory(
                    services.GetRequiredService<PullingAgentMigrationState>(),
                    services.GetRequiredService<IGrainContextAccessor>()),
                configurator =>
                {
                    configurator.ConfigurePullingAgent(options => options.Configure(value => value.HostingMode = StreamPullingAgentHostingMode.Grain));
                    configurator.ConfigureLifecycle(options => options.Configure(value =>
                        value.StartupState = StreamLifecycleOptions.RunState.AgentsStopped));
                    configurator.ConfigureStreamPubSub(StreamPubSubType.ExplicitGrainBasedOnly);
                    configurator.ConfigurePartitionBalancing((_, _) =>
                        throw new InvalidOperationException("Grain hosting must not construct a queue balancer."));
                });
        });
        await using var cluster = builder.Build();
        using var events = new DiagnosticEventCollector(StreamingEvents.ListenerName, GrainLifecycleEvents.ListenerName);
        try
        {
            await cluster.DeployAsync(TestContext.Current.CancellationToken);
            await Wait(cluster.WaitForLivenessToStabilizeAsync(), "production host cluster liveness");
            await Wait(cluster.WaitForClusterManifestToStabilizeAsync(), "production host provider manifest");
            state.SourceSilo = cluster.Silos[0].SiloAddress;
            state.DestinationSilo = cluster.Silos[1].SiloAddress;
            var consumer = cluster.Client.GetGrain<IMigrationPullingAgentConsumer>(Guid.NewGuid());
            var relay = cluster.Client.GetGrain<IMigrationPullingAgentRelay>(Guid.NewGuid());
            RequestContext.Set(IPlacementDirector.PlacementHintKey, state.DestinationSilo);
            try
            {
                Assert.Equal(state.DestinationSilo, (await Wait(consumer.GetAddress(), "production consumer placement")).SiloAddress);
                Assert.Equal(state.DestinationSilo, (await Wait(relay.GetAddress(), "production relay placement")).SiloAddress);
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            await Wait(consumer.SubscribeToProvider(), "real rendezvous consumer registration");
            var sourceDrained = events.WaitForEventAsync(
                nameof(StreamingEvents.ConsumerCursorDrained),
                evt => evt.Payload is StreamingEvents.ConsumerCursorDrained value
                    && value.StreamId == state.StreamId && value.SiloAddress == state.SourceSilo,
                PhaseTimeout,
                TestContext.Current.CancellationToken);
            await Wait(cluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IControllable>(PullingAgentMigrationState.ProviderName)
                .ExecuteCommand((int)PersistentStreamProviderCommand.StartAgents, null), "starting production provider on source");
            await Wait(sourceDrained, "production host acknowledgement through 100");
            var stableId = PullingAgentId.Create(PullingAgentMigrationState.ProviderName, PullingAgentMigrationState.QueueId);
            var original = state.SourceSession.Address;
            Assert.Equal(stableId, original.GrainId);
            Assert.True(cluster.TryGetGrainContext(stableId, out var context));
            Assert.IsType<PullingAgentGrain>(context.GrainInstance);
            Assert.Equal(1, await Wait(consumer.GetProducerCount(), "initial real rendezvous producer count"));
            Assert.Equal(20, state.DurableOffset);

            var destinationAttached = events.WaitForEventAsync(
                nameof(StreamingEvents.SubscriptionAttached),
                evt => evt.Payload is StreamingEvents.SubscriptionAttached value
                    && value.StreamId == state.StreamId && value.SiloAddress == state.DestinationSilo,
                PhaseTimeout,
                TestContext.Current.CancellationToken);
            await Wait(cluster.Silos[1].ServiceProvider.GetRequiredKeyedService<IControllable>(PullingAgentMigrationState.ProviderName)
                .ExecuteCommand((int)PersistentStreamProviderCommand.StartAgents, null), "starting production provider on destination");
            Assert.True(await Wait(cluster.Client.GetGrain<IPullingAgentGrain>(stableId)
                .Rebalance(original, state.DestinationSilo, TestContext.Current.CancellationToken), "explicit production-host rebalance"));
            await Wait(state.FlushEntered.Task, "production host final checkpoint flush");
            Assert.Equal(100, state.SourceSession.AcknowledgedOffset);
            var sourceReadCount = state.SourceSession.ReadCount;
            var probes = relay.ProbeHostedProducer(stableId, state.DestinationSilo);
            await Wait(state.ProbesIssued.Task, "production stable-ID calls during blocked checkpoint flush");
            Assert.False(probes.IsCompleted);
            Assert.Equal(original, state.DirectoryAddressDuringFlush);
            Assert.Equal(20, state.DurableOffset);
            Assert.Equal(0, state.CheckpointLoadsOn(state.DestinationSilo));
            Assert.Equal(0, state.SourceReadsOn(state.DestinationSilo));
            Assert.Equal(sourceReadCount, state.SourceSession.ReadCount);

            state.ReleaseFlush.TrySetResult();
            var addresses = await Wait(probes, "production-host calls after checkpoint handoff");
            await Wait(state.DestinationRead.Task, "production destination inclusive source read");
            await Wait(destinationAttached, "production destination recovery handshake");
            Assert.Equal(100, state.DurableOffset);
            Assert.Equal(1, state.SuccessfulFlushes);
            Assert.Equal(1, state.CheckpointLoadsOn(state.DestinationSilo));
            var destination = Assert.Single(state.Sessions, session => session.Address.SiloAddress == state.DestinationSilo);
            Assert.Equal(100, destination.LoadedOffset);
            Assert.Equal(new long[] { 100 }, destination.ReadOffsets.ToArray());
            Assert.Equal(new long[] { 20, 100 }, state.DeliveredOffsets.ToArray());
            Assert.Equal(1, await Wait(consumer.GetProducerCount(), "retained stable rendezvous producer registration"));
            Assert.All(addresses, address =>
            {
                Assert.Equal(stableId, address.GrainId);
                Assert.Equal(state.DestinationSilo, address.SiloAddress);
                Assert.NotEqual(original.ActivationId, address.ActivationId);
                Assert.Equal(destination.Address.ActivationId, address.ActivationId);
            });
        }
        finally
        {
            state.ReleaseFlush.TrySetResult();
        }
    }

    [Fact]
    public async Task Shutdown_PropagatesFinalCheckpointFailureToCaller()
    {
        var state = new PullingAgentMigrationState { FailSourceFlush = true };
        await using var cluster = CreateCluster(state);
        using var events = new DiagnosticEventCollector(StreamingEvents.ListenerName, GrainLifecycleEvents.ListenerName);
        try
        {
            var (producer, _, original) = await StartScenario(cluster, state, events);
            var shutdown = producer.ShutdownEngine();
            await Wait(state.FlushEntered.Task, $"flush entry for {original}");
            Assert.Equal(20, state.DurableOffset);
            Assert.Equal(100, state.SourceSession.AcknowledgedOffset);
            state.ReleaseFlush.TrySetResult();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Wait(shutdown, $"failed engine shutdown for {original}"));
            Assert.Equal(PullingAgentMigrationState.FlushFailureMessage, exception.Message);
            Assert.Equal(20, state.DurableOffset);
            Assert.Equal(0, state.SuccessfulFlushes);
            Assert.Equal(new[] { original.GrainId }, state.UnregisteredProducers.ToArray());
        }
        finally
        {
            state.ReleaseFlush.TrySetResult();
        }
    }

    private static async Task RunHandoff(bool stopSourceSilo)
    {
        var state = new PullingAgentMigrationState();
        await using var cluster = CreateCluster(state);
        using var events = new DiagnosticEventCollector(StreamingEvents.ListenerName, GrainLifecycleEvents.ListenerName);
        Task stopTask = Task.CompletedTask;
        try
        {
            var (producer, relay, original) = await StartScenario(cluster, state, events);
            var stableId = producer.GetGrainId();
            var source = cluster.Silos.Single(silo => silo.SiloAddress == original.SiloAddress);
            var destination = cluster.Silos.Single(silo => silo.SiloAddress != original.SiloAddress);
            var destinationAttached = events.WaitForEventAsync(
                nameof(StreamingEvents.SubscriptionAttached),
                evt => evt.Payload is StreamingEvents.SubscriptionAttached value
                    && value.StreamId == state.StreamId
                    && value.SiloAddress == destination.SiloAddress,
                PhaseTimeout,
                TestContext.Current.CancellationToken);
            var deactivated = events.WaitForEventAsync(
                nameof(GrainLifecycleEvents.Deactivated),
                evt => evt.Payload is GrainLifecycleEvents.Deactivated value
                    && value.GrainContext.Address.Equals(original),
                PhaseTimeout,
                TestContext.Current.CancellationToken);

            if (stopSourceSilo)
            {
                stopTask = cluster.StopSiloAsync(source, TestContext.Current.CancellationToken);
            }
            else
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, destination.SiloAddress);
                try
                {
                    await Wait(
                        producer.Cast<IGrainManagementExtension>().MigrateOnIdle(TestContext.Current.CancellationToken).AsTask(),
                        $"MigrateOnIdle request for {original}");
                }
                finally
                {
                    RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                }
            }

            await Wait(state.FlushEntered.Task, $"final checkpoint flush entry for {original}");
            var sourceReadCount = state.SourceSession.ReadCount;
            Assert.Equal(20, state.DurableOffset);
            Assert.Equal(100, state.SourceSession.AcknowledgedOffset);
            Assert.False(deactivated.IsCompleted, $"Source deactivated before its checkpoint flush: {original}");

            // These are ordinary grain-reference calls originating on the other silo, not direct
            // calls on the source instance or references pinned to its activation.
            var probes = relay.ProbeProducer(stableId);
            await Wait(state.ProbesIssued.Task, $"three stable-ID calls from {destination.SiloAddress} to {stableId}");
            Assert.False(probes.IsCompleted, $"Stable-ID calls escaped the blocked handoff for {original}");
            if (!stopSourceSilo)
            {
                Assert.Equal(original, state.DirectoryAddressDuringFlush);
            }

            Assert.Equal(0, state.CheckpointLoadsOn(destination.SiloAddress));
            Assert.Equal(0, state.SourceReadsOn(destination.SiloAddress));
            Assert.Equal(sourceReadCount, state.SourceSession.ReadCount);
            Assert.Equal(0, state.SuccessfulFlushes);

            state.ReleaseFlush.TrySetResult();
            await Wait(deactivated, $"source deactivation after checkpoint flush for {original}");
            GrainAddress[] addresses;
            try
            {
                addresses = await Wait(probes, $"stable-ID calls completing on {destination.SiloAddress}");
            }
            catch (SiloUnavailableException) when (stopSourceSilo)
            {
                // Graceful transport shutdown can fail outstanding RPCs even after migrating the
                // activation. Retry only after the shutdown and membership barriers have completed.
                await Wait(stopTask, $"source transport shutdown for {original}");
                await Wait(cluster.WaitForLivenessToStabilizeAsync(), "surviving silo membership");
                addresses = await Wait(relay.ProbeProducer(stableId), $"post-shutdown stable-ID calls to {stableId}");
            }

            await Wait(state.DestinationRead.Task, $"inclusive destination source read for {stableId}");
            await Wait(destinationAttached, $"destination consumer recovery handshake for {stableId}");
            await Wait(stopTask, $"graceful source silo shutdown for {original}");
            var finalAddress = await Wait(producer.GetAddress(), $"final stable-ID lookup for {stableId}");

            Assert.Equal(100, state.DurableOffset);
            Assert.Equal(1, state.SuccessfulFlushes);
            Assert.Equal(1, state.CheckpointLoadsOn(destination.SiloAddress));
            var destinationSession = Assert.Single(state.Sessions, session => session.Address.SiloAddress == destination.SiloAddress);
            Assert.Equal(100, destinationSession.LoadedOffset);
            Assert.Equal(new long[] { 100 }, destinationSession.ReadOffsets.ToArray());
            Assert.Equal(new long[] { 20, 100 }, state.DeliveredOffsets.ToArray());
            Assert.All(addresses, address =>
            {
                Assert.Equal(stableId, address.GrainId);
                Assert.Equal(destination.SiloAddress, address.SiloAddress);
                Assert.NotEqual(original.ActivationId, address.ActivationId);
                Assert.Equal(destinationSession.Address.ActivationId, address.ActivationId);
            });
            Assert.Equal(new[] { stableId, stableId }, state.RegisteredProducers.ToArray());
            Assert.Empty(state.UnregisteredProducers);
            Assert.Equal(stableId, producer.GetGrainId());
            Assert.Equal(destinationSession.Address, finalAddress);
            if (stopSourceSilo)
            {
                Assert.False(source.IsActive);
                Assert.Equal(DeactivationReasonCode.ShuttingDown, state.SourceDeactivationReason);
            }
        }
        finally
        {
            state.ReleaseFlush.TrySetResult();
            await Wait(stopTask, "source silo shutdown cleanup");
        }
    }

    private static async Task RunInterruptedHandoff(bool blockRegistration)
    {
        var state = new PullingAgentMigrationState
        {
            BlockSourceRegistration = blockRegistration,
            BlockSourceDelivery = !blockRegistration,
        };
        await using var cluster = CreateCluster(state);
        using var events = new DiagnosticEventCollector(StreamingEvents.ListenerName, GrainLifecycleEvents.ListenerName);
        try
        {
            var (producer, relay, original) = await StartScenario(cluster, state, events);
            var stableId = producer.GetGrainId();
            var deactivated = events.WaitForEventAsync(
                nameof(GrainLifecycleEvents.Deactivated),
                evt => evt.Payload is GrainLifecycleEvents.Deactivated value && value.GrainContext.Address.Equals(original),
                PhaseTimeout,
                TestContext.Current.CancellationToken);
            var destinationDrained = events.WaitForEventAsync(
                nameof(StreamingEvents.ConsumerCursorDrained),
                evt => evt.Payload is StreamingEvents.ConsumerCursorDrained value
                    && value.StreamId == state.StreamId && value.SiloAddress == state.DestinationSilo,
                PhaseTimeout,
                TestContext.Current.CancellationToken);

            RequestContext.Set(IPlacementDirector.PlacementHintKey, state.DestinationSilo);
            try
            {
                await Wait(
                    producer.Cast<IGrainManagementExtension>().MigrateOnIdle(TestContext.Current.CancellationToken).AsTask(),
                    $"migration with interrupted work for {original}");
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            await Wait(state.InFlightCanceled.Task, $"shutdown cancellation reaching held work for {original}");
            await Wait(state.FlushEntered.Task, $"conservative flush while remote work remains held for {original}");
            Assert.False(state.InFlightFinished.Task.IsCompleted);
            Assert.False(deactivated.IsCompleted);
            Assert.Equal(20, state.DurableOffset);
            if (blockRegistration)
            {
                Assert.Null(state.SourceSession.AcknowledgedOffset);
            }
            else
            {
                Assert.Equal(20, state.SourceSession.AcknowledgedOffset);
            }

            var probes = relay.ProbeProducer(stableId);
            await Wait(state.ProbesIssued.Task, $"stable-ID probes during interrupted handoff for {original}");
            Assert.False(probes.IsCompleted);
            Assert.Equal(original, state.DirectoryAddressDuringFlush);
            Assert.Equal(0, state.CheckpointLoadsOn(state.DestinationSilo));
            Assert.Equal(0, state.SourceReadsOn(state.DestinationSilo));

            state.ReleaseFlush.TrySetResult();
            await Wait(deactivated, $"source deactivation with canceled remote work for {original}");
            Assert.False(state.InFlightFinished.Task.IsCompleted);
            state.ReleaseInFlight.TrySetResult();
            await Wait(state.InFlightFinished.Task, $"held remote work completion for {original}");
            var addresses = await Wait(probes, $"stable-ID calls after interrupted handoff for {original}");
            await Wait(destinationDrained, $"destination delivery recovery from durable offset 20 for {stableId}");

            Assert.Equal(20, state.DurableOffset);
            Assert.Equal(1, state.SuccessfulFlushes);
            Assert.Equal(1, state.CheckpointLoadsOn(state.DestinationSilo));
            var destination = Assert.Single(state.Sessions, session => session.Address.SiloAddress == state.DestinationSilo);
            Assert.Equal(20, destination.LoadedOffset);
            Assert.Equal(new long[] { 20, 100 }, destination.ReadOffsets.ToArray());
            Assert.Equal(new long[] { 20, 100 }, state.DeliveredOffsets.ToArray());
            Assert.Empty(state.UnregisteredProducers);
            Assert.All(addresses, address =>
            {
                Assert.Equal(stableId, address.GrainId);
                Assert.Equal(state.DestinationSilo, address.SiloAddress);
                Assert.NotEqual(original.ActivationId, address.ActivationId);
                Assert.Equal(destination.Address.ActivationId, address.ActivationId);
            });
        }
        finally
        {
            state.ReleaseInFlight.TrySetResult();
            state.ReleaseFlush.TrySetResult();
        }
    }

    private static InProcessTestCluster CreateCluster(PullingAgentMigrationState state)
    {
        var builder = new InProcessTestClusterBuilder(2);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003
        builder.ConfigureSilo((_, silo) => silo.Services.AddSingleton(state));
        return builder.Build();
    }

    private static async Task<(IMigrationPullingAgentGrain Producer, IMigrationPullingAgentRelay Relay, GrainAddress Original)> StartScenario(
        InProcessTestCluster cluster,
        PullingAgentMigrationState state,
        DiagnosticEventCollector events)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await cluster.DeployAsync(cancellationToken);
        await Wait(cluster.WaitForLivenessToStabilizeAsync(), "two-silo liveness");
        await Wait(cluster.WaitForClusterManifestToStabilizeAsync(), "two-silo cluster manifest");
        var expectedMembers = cluster.Silos.Select(silo => silo.SiloAddress).ToHashSet();
        await Wait(Task.WhenAll(cluster.Silos.Select(async silo =>
        {
            var membership = silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
            if (expectedMembers.SetEquals(membership.CurrentView.Members))
            {
                return;
            }

            await foreach (var view in membership.ViewUpdates.WithCancellation(cancellationToken))
            {
                if (expectedMembers.SetEquals(view.Members))
                {
                    return;
                }
            }

            throw new InvalidOperationException($"Directory membership ended before both silos were visible on {silo.SiloAddress}.");
        })), "real distributed directory membership");

        state.SourceSilo = cluster.Silos[0].SiloAddress;
        var destination = cluster.Silos[1].SiloAddress;
        state.DestinationSilo = destination;
        var consumer = cluster.Client.GetGrain<IMigrationPullingAgentConsumer>(Guid.NewGuid());
        var relay = cluster.Client.GetGrain<IMigrationPullingAgentRelay>(Guid.NewGuid());
        RequestContext.Set(IPlacementDirector.PlacementHintKey, destination);
        try
        {
            Assert.Equal(destination, (await Wait(consumer.GetAddress(), "consumer placement")).SiloAddress);
            Assert.Equal(destination, (await Wait(relay.GetAddress(), "relay placement")).SiloAddress);
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        state.ConsumerId = consumer.GetGrainId();
        Task ready = state.BlockSourceRegistration || state.BlockSourceDelivery
            ? state.InFlightStarted.Task
            : events.WaitForEventAsync(
                nameof(StreamingEvents.ConsumerCursorDrained),
                evt => evt.Payload is StreamingEvents.ConsumerCursorDrained value
                    && value.StreamId == state.StreamId
                    && value.SiloAddress == state.SourceSilo,
                PhaseTimeout,
                cancellationToken);
        var producer = cluster.Client.GetGrain<IMigrationPullingAgentGrain>(Guid.NewGuid());
        RequestContext.Set(IPlacementDirector.PlacementHintKey, state.SourceSilo);
        GrainAddress original;
        try
        {
            original = await Wait(producer.GetAddress(), "initial producer activation");
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        await Wait(ready, $"source delivery/registration readiness for {original}");
        Assert.Equal(state.SourceSilo, original.SiloAddress);
        Assert.Equal(producer.GetGrainId(), original.GrainId);
        Assert.Equal(1, state.CheckpointLoadsOn(state.SourceSilo));
        Assert.Equal(20, state.SourceSession.LoadedOffset);
        Assert.Equal(new long[] { 20, 100 }, state.SourceSession.ReadOffsets.ToArray());
        long[] expectedDelivery = state.BlockSourceRegistration ? [] : state.BlockSourceDelivery ? [20] : [20, 100];
        Assert.Equal(expectedDelivery, state.DeliveredOffsets.ToArray());
        Assert.Equal(20, state.DurableOffset);
        Assert.Contains(original.GrainId, state.RegisteredProducers);
        return (producer, relay, original);
    }

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

public interface IMigrationPullingAgentGrain : IGrainWithGuidKey
{
    Task<GrainAddress> GetAddress();
    Task ShutdownEngine();
}

public sealed class MigrationPullingAgentGrain : Grain, IMigrationPullingAgentGrain, IStreamProducerExtension
{
    private readonly PullingAgentMigrationState _state;
    private PersistentStreamPullingAgent _engine = null!;

    public MigrationPullingAgentGrain(PullingAgentMigrationState state) => _state = state;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var session = new MigrationReceiverSession(GrainContext.Address);
        _state.Sessions.Enqueue(session);
        var adapter = new MigrationQueueAdapter(_state, session);
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(default, default, default).ReturnsForAnyArgs(call =>
            _state.RegisterProducer(session, call.Arg<GrainId>(), call.Arg<CancellationToken>()));
        pubSub.UnregisterProducer(default, default, default).ReturnsForAnyArgs(call =>
        {
            _state.UnregisteredProducers.Enqueue(call.Arg<GrainId>());
            return Task.CompletedTask;
        });
        _engine = new PersistentStreamPullingAgent(
            GrainContext,
            PullingAgentMigrationState.ProviderName,
            pubSub,
            new NoOpStreamFilter(),
            PullingAgentMigrationState.QueueId,
            new StreamPullingAgentOptions(),
            adapter,
            adapter,
            new NoOpStreamDeliveryFailureHandler(),
            Substitute.For<IBackoffProvider>(),
            Substitute.For<IBackoffProvider>(),
            TimeProvider.System,
            ServiceProvider.GetRequiredService<ILoggerFactory>(),
            ServiceProvider.GetRequiredService<ITimerRegistry>(),
            GrainFactory);
        await _engine.Initialize(cancellationToken, waitForReceiver: true);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (GrainContext.Address.SiloAddress == _state.SourceSilo)
        {
            _state.SourceDeactivationReason = reason.ReasonCode;
        }

        await _engine.Shutdown(cancellationToken, suppressReceiverShutdownErrors: false, unregisterProducer: false);
        if (reason.ReasonCode == DeactivationReasonCode.ShuttingDown
            && GrainContext.Address.SiloAddress == _state.SourceSilo)
        {
            GrainContext.Migrate(new()
            {
                [IPlacementDirector.PlacementHintKey] = _state.DestinationSilo,
            }, cancellationToken);
        }
    }

    public Task<GrainAddress> GetAddress() => Task.FromResult(GrainContext.Address);

    public Task ShutdownEngine() => _engine.Shutdown(CancellationToken.None, suppressReceiverShutdownErrors: false);

    public Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
        => _engine.AddSubscriber(subscriptionId, streamId, streamConsumer, filterData, cancellationToken);

    public Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
        => _engine.RemoveSubscriber(subscriptionId, streamId, cancellationToken);
}

public interface IMigrationPullingAgentRelay : IGrainWithGuidKey
{
    Task<GrainAddress> GetAddress();
    Task<GrainAddress[]> ProbeProducer(GrainId producerId);
    Task<GrainAddress[]> ProbeHostedProducer(GrainId producerId, SiloAddress requestedHost);
}

public sealed class MigrationPullingAgentRelay(PullingAgentMigrationState state) : Grain, IMigrationPullingAgentRelay
{
    public Task<GrainAddress> GetAddress() => Task.FromResult(GrainContext.Address);

    public async Task<GrainAddress[]> ProbeProducer(GrainId producerId)
    {
        var producer = GrainFactory.GetGrain<IMigrationPullingAgentGrain>(producerId);
        Task<GrainAddress>[] calls = [producer.GetAddress(), producer.GetAddress(), producer.GetAddress()];
        var directory = Assert.IsType<DistributedGrainDirectory>(
            ServiceProvider.GetRequiredService<GrainDirectoryResolver>().Resolve(producerId.Type));
        state.DirectoryAddressDuringFlush = await directory.Lookup(producerId, CancellationToken.None);
        state.ProbesIssued.TrySetResult();
        return await Task.WhenAll(calls);
    }

    public async Task<GrainAddress[]> ProbeHostedProducer(GrainId producerId, SiloAddress requestedHost)
    {
        var producer = GrainFactory.GetGrain<IPullingAgentGrain>(producerId);
        Task<PullingAgentStatus>[] calls;
        RequestContext.Set(IPlacementDirector.PlacementHintKey, requestedHost);
        try
        {
            calls = [producer.Probe(), producer.Probe(), producer.Probe()];
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }
        var directory = Assert.IsType<DistributedGrainDirectory>(
            ServiceProvider.GetRequiredService<GrainDirectoryResolver>().Resolve(producerId.Type));
        state.DirectoryAddressDuringFlush = await directory.Lookup(producerId, CancellationToken.None);
        state.ProbesIssued.TrySetResult();
        var statuses = await Task.WhenAll(calls);
        Assert.All(statuses, status => Assert.True(status.IsRunning));
        return statuses.Select(status => status.Address).ToArray();
    }
}

public interface IMigrationPullingAgentConsumer : IGrainWithGuidKey
{
    Task<GrainAddress> GetAddress();
    Task SubscribeToProvider();
    Task<int> GetProducerCount();
}

public sealed class MigrationPullingAgentConsumer(PullingAgentMigrationState state) : Grain, IMigrationPullingAgentConsumer, IStreamConsumerExtension
{
    private StreamSequenceToken? _acknowledged;

    public Task<GrainAddress> GetAddress() => Task.FromResult(GrainContext.Address);

    public Task SubscribeToProvider() => ServiceProvider.GetRequiredService<IStreamProviderRuntime>()
        .PubSub(StreamPubSubType.ExplicitGrainBasedOnly)!
        .RegisterConsumer(state.SubscriptionId, new QualifiedStreamId(PullingAgentMigrationState.ProviderName, state.StreamId), GrainContext.GrainId, null, CancellationToken.None);

    public Task<int> GetProducerCount() => ServiceProvider.GetRequiredService<IStreamProviderRuntime>()
        .PubSub(StreamPubSubType.ExplicitGrainBasedOnly)!
        .ProducerCount(new QualifiedStreamId(PullingAgentMigrationState.ProviderName, state.StreamId), CancellationToken.None);

    Task<StreamHandshakeToken?> IStreamConsumerExtension.GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
        => Task.FromResult(_acknowledged is null ? null : StreamHandshakeToken.CreateDeliveyToken(_acknowledged));

    async Task<StreamHandshakeToken?> IStreamConsumerExtension.DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
    {
        if (state.BlockSourceDelivery && item.SequenceToken.SequenceNumber == 100
            && Interlocked.CompareExchange(ref state.DeliveryBlockStarted, 1, 0) == 0)
        {
            await state.HoldInFlightOperation(cancellationToken);
        }

        state.DeliveredOffsets.Enqueue(item.SequenceToken.SequenceNumber);
        _acknowledged = item.SequenceToken;
        return null;
    }

    Task<StreamHandshakeToken?> IStreamConsumerExtension.DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
        => throw new NotSupportedException("This test consumer receives queue batches.");

    Task<StreamHandshakeToken?> IStreamConsumerExtension.DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
        => throw new NotSupportedException("This test consumer receives queue batches.");

    public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken)
        => Task.FromException(exc);
}

public sealed class PullingAgentMigrationState
{
    internal const string ProviderName = "pulling-agent-migration";
    internal const string FlushFailureMessage = "Controlled checkpoint flush failed.";
    internal static readonly QueueId QueueId = QueueId.GetQueueId("migration", 0, 0);
    private long _durableOffset = 20;
    private int _successfulFlushes;

    internal StreamId StreamId { get; } = StreamId.Create("migration", Guid.NewGuid());
    internal GuidId SubscriptionId { get; } = GuidId.GetNewGuidId();
    internal GrainId ConsumerId { get; set; }
    internal GrainAddress? DirectoryAddressDuringFlush { get; set; }
    internal SiloAddress SourceSilo { get; set; } = null!;
    internal SiloAddress DestinationSilo { get; set; } = null!;
    internal DeactivationReasonCode SourceDeactivationReason { get; set; }
    internal bool FailSourceFlush { get; init; }
    internal bool BlockSourceRegistration { get; init; }
    internal bool BlockSourceDelivery { get; init; }
    internal int DeliveryBlockStarted;
    internal ConcurrentQueue<MigrationReceiverSession> Sessions { get; } = new();
    internal ConcurrentQueue<GrainId> RegisteredProducers { get; } = new();
    internal ConcurrentQueue<GrainId> UnregisteredProducers { get; } = new();
    internal ConcurrentQueue<long> DeliveredOffsets { get; } = new();
    internal TaskCompletionSource FlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ProbesIssued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource DestinationRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource InFlightStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource InFlightCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource InFlightFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseInFlight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal long DurableOffset => Interlocked.Read(ref _durableOffset);
    internal int SuccessfulFlushes => Volatile.Read(ref _successfulFlushes);
    internal MigrationReceiverSession SourceSession => Sessions.Single(session => session.Address.SiloAddress == SourceSilo);

    internal int CheckpointLoadsOn(SiloAddress silo) => Sessions.Where(session => session.Address.SiloAddress == silo).Sum(session => session.LoadCount);
    internal int SourceReadsOn(SiloAddress silo) => Sessions.Where(session => session.Address.SiloAddress == silo).Sum(session => session.ReadCount);

    internal async Task<ISet<PubSubSubscriptionState>> RegisterProducer(
        MigrationReceiverSession session,
        GrainId producer,
        CancellationToken cancellationToken)
    {
        RegisteredProducers.Enqueue(producer);
        if (BlockSourceRegistration && session.Address.SiloAddress == SourceSilo)
        {
            await HoldInFlightOperation(cancellationToken);
        }

        return new HashSet<PubSubSubscriptionState>
        {
            new(SubscriptionId, new QualifiedStreamId(ProviderName, StreamId), ConsumerId),
        };
    }

    internal async Task HoldInFlightOperation(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => InFlightCanceled.TrySetResult());
        InFlightStarted.TrySetResult();
        try
        {
            // Model a remote operation which remains occupied after its caller stops awaiting it.
            await ReleaseInFlight.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            InFlightFinished.TrySetResult();
        }
    }

    internal async Task Flush(MigrationReceiverSession session, CancellationToken cancellationToken)
    {
        if (session.Address.SiloAddress != SourceSilo)
        {
            return;
        }

        FlushEntered.TrySetResult();
        await ReleaseFlush.Task.WaitAsync(cancellationToken);
        if (FailSourceFlush)
        {
            throw new InvalidOperationException(FlushFailureMessage);
        }

        if (session.AcknowledgedOffset is { } acknowledged)
        {
            Interlocked.Exchange(ref _durableOffset, acknowledged);
        }

        Interlocked.Increment(ref _successfulFlushes);
    }
}

internal sealed class MigrationReceiverSession(GrainAddress address)
{
    internal GrainAddress Address { get; } = address;
    internal long LoadedOffset { get; set; }
    internal long? AcknowledgedOffset { get; set; }
    internal int LoadCount;
    internal int ReadCount;
    internal ConcurrentQueue<long> ReadOffsets { get; } = new();
}

internal sealed class MigrationQueueAdapter(PullingAgentMigrationState state, MigrationReceiverSession session) : IQueueAdapter, IQueueAdapterCache
{
    public string Name => PullingAgentMigrationState.ProviderName;
    public bool IsRewindable => true;
    public StreamProviderDirection Direction => StreamProviderDirection.ReadOnly;
    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) => new MigrationQueueReceiver(state, session);
    public IQueueCache CreateQueueCache(QueueId queueId) => new MigrationQueueCache(session);
    public Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
        => throw new NotSupportedException("The migration test uses a controlled read-only source.");
}

internal sealed class HostedMigrationAdapterFactory(PullingAgentMigrationState state, IGrainContextAccessor contextAccessor) : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache, IStreamQueueMapper
{
    private readonly ConcurrentDictionary<ActivationId, MigrationReceiverSession> _sessions = new();

    public string Name => PullingAgentMigrationState.ProviderName;
    public bool IsRewindable => true;
    public StreamProviderDirection Direction => StreamProviderDirection.ReadOnly;
    public Task<IQueueAdapter> CreateAdapter() => Task.FromResult<IQueueAdapter>(this);
    public Task<IQueueAdapter> CreateAdapter(CancellationToken cancellationToken) => Task.FromResult<IQueueAdapter>(this);
    public IQueueAdapterCache GetQueueAdapterCache() => this;
    public IStreamQueueMapper GetStreamQueueMapper() => this;
    public IEnumerable<QueueId> GetAllQueues() => [PullingAgentMigrationState.QueueId];
    public QueueId GetQueueForStream(StreamId streamId) => PullingAgentMigrationState.QueueId;
    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());

    public IQueueCache CreateQueueCache(QueueId queueId)
    {
        var context = contextAccessor.GrainContext;
        Assert.Equal(PullingAgentId.GrainType, context.GrainId.Type);
        var session = new MigrationReceiverSession(context.Address);
        Assert.True(_sessions.TryAdd(context.ActivationId, session));
        state.Sessions.Enqueue(session);
        return new MigrationQueueCache(session);
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        => new MigrationQueueReceiver(state, _sessions[contextAccessor.GrainContext.ActivationId]);

    public Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
        => throw new NotSupportedException("The migration test uses a controlled read-only source.");
}

internal sealed class MigrationQueueReceiver(PullingAgentMigrationState state, MigrationReceiverSession session) : IQueueAdapterReceiver
{
    private bool _read;

    public Task Initialize(TimeSpan timeout) => Initialize(timeout, CancellationToken.None);

    public Task Initialize(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        session.LoadedOffset = state.DurableOffset;
        Interlocked.Increment(ref session.LoadCount);
        return Task.CompletedTask;
    }

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount) => GetQueueMessagesAsync(maxCount, CancellationToken.None);

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref session.ReadCount);
        IList<IBatchContainer> result = [];
        if (!_read)
        {
            _read = true;
            foreach (var offset in new long[] { 20, 100 }.Where(offset => offset >= session.LoadedOffset))
            {
                session.ReadOffsets.Enqueue(offset);
                result.Add(new GeneratedBatchContainer(state.StreamId, offset, new EventSequenceTokenV2(offset)));
            }

            if (session.Address.SiloAddress != state.SourceSilo)
            {
                state.DestinationRead.TrySetResult();
            }
        }

        return Task.FromResult(result);
    }

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) => Task.CompletedTask;
    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task Shutdown(TimeSpan timeout) => Shutdown(timeout, CancellationToken.None);
    public Task Shutdown(TimeSpan timeout, CancellationToken cancellationToken) => state.Flush(session, cancellationToken);
}

internal sealed class MigrationQueueCache(MigrationReceiverSession session) : IQueueCache
{
    private readonly List<IBatchContainer> _batches = [];

    public void AddToCache(IList<IBatchContainer> messages) => _batches.AddRange(messages);
    public int GetMaxAddCount() => 2;
    public bool IsUnderPressure() => false;
    public bool TryPurgeFromCache([MaybeNullWhen(false)] out IList<IBatchContainer> purgedItems)
    {
        purgedItems = null;
        return false;
    }

    public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token) => new Cursor(_batches, token);
    public QueueCacheCursorResult<IQueueCacheCursor> TryGetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        => QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new Cursor(_batches, token));

    public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        => session.AcknowledgedOffset = earliestSubscriptionToken?.SequenceNumber;

    private sealed class Cursor(List<IBatchContainer> batches, StreamSequenceToken? token) : IQueueCacheCursor
    {
        private int _index = -1;

        public bool MoveNext() => MoveNextWithResult().Kind == QueueCacheCursorMoveResultKind.Success;

        public QueueCacheCursorMoveResult MoveNextWithResult()
        {
            for (var next = _index + 1; next < batches.Count; next++)
            {
                _index = next;
                if (token is null || batches[next].SequenceToken.SequenceNumber >= token.SequenceNumber)
                {
                    return QueueCacheCursorMoveResult.Success;
                }
            }

            return QueueCacheCursorMoveResult.NoData;
        }

        public IBatchContainer? GetCurrent(out Exception? exception)
        {
            exception = null;
            return _index >= 0 && _index < batches.Count ? batches[_index] : null;
        }

        public void Refresh(StreamSequenceToken nextToken) { }
        public void RecordDeliveryFailure() => throw new InvalidOperationException("Unexpected delivery failure in the migration test.");
        public void Dispose() { }
    }
}
