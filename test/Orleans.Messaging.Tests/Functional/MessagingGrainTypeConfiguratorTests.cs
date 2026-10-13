using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Concurrency;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public class MessagingGrainTypeConfiguratorTests : MessagingBehaviorTestBase
{
    public MessagingGrainTypeConfiguratorTests() : this(new BootstrapClusterFixture())
    {
    }

    protected MessagingGrainTypeConfiguratorTests(BootstrapClusterFixture fixture) : base(fixture)
    {
    }

    private BootstrapDeliveryProbe Delivery => ((BootstrapClusterFixture)Fixture).Delivery;
    private BootstrapProbe Probe => ((BootstrapClusterFixture)Fixture).Probe;
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(PlainBootstrapGrain))]
    [InlineData(typeof(ApplicationBootstrapGrain))]
    [InlineData(typeof(InterfaceBootstrapGrain))]
    [InlineData(typeof(GenericBootstrapGrain<int>))]
    [InlineData(typeof(DurableBootstrapGrain))]
    [InlineData(typeof(MarkedDurableBootstrapGrain))]
    public async Task SelectedComposition_InstallsInboxExtensionAndReplaysFreshScopedState(Type grainClass)
    {
        var grain = CreateGrain(grainClass);
        Assert.Equal(0, await grain.GetValueAsync());
        var first = Assert.Single(Probe.Get(grain.GetGrainId()));
        var state = first.Context.ActivationServices.GetRequiredService<BootstrapState>();
        AssertComposition(first, state, grainClass, expectedActivationValue: 0);
        var setup = GetSetup(first.Context);
        Assert.Single(setup.GetInvocationList());
        await grain.SetValueAsync(41);
        var journal = JournalId.FromGrainId(grain.GetGrainId());

        using var handler = Fixture.HandlerProbe.Arm(grain.GetGrainId(), BootstrapState.Route);
        var envelope = CreateEnvelope(grain);
        Assert.Equal(DeliveryStatus.Accepted, (await grain.AsReference<IInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        await handler.WaitUntilEnteredAsync();
        Assert.Equal(41, first.Value!.Value);
        Assert.Equal(1, first.Inbox!.Count);
        Assert.Equal(0, first.Outbox!.Count());
        Assert.Equal(0, GetProcessedCount(first));
        Assert.Equal(0, state.HandlerCalls);
        handler.Release();
        await Fixture.WaitUntilAsync(grain, () => state.HandlerCalls == 1 && first.Inbox!.Count == 0);
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(1, GetProcessedCount(first));
        var output = Assert.Single(first.Outbox!.Messages());
        Assert.NotEmpty(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grain.GetGrainId()));
        Assert.Equal(DeliveryStatus.Duplicate, (await grain.AsReference<IInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        Assert.Equal(1, state.HandlerCalls);

        var other = CreateGrain(grainClass);
        Assert.Equal(0, await other.GetValueAsync());
        var isolated = Assert.Single(Probe.Get(other.GetGrainId()));
        Assert.NotSame(first.Manager, isolated.Manager);
        Assert.NotSame(first.Inbox, isolated.Inbox);
        Assert.NotSame(first.Outbox, isolated.Outbox);
        Assert.Empty(isolated.Outbox!.Messages());
        await other.DeactivateAsync();
        await isolated.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        await grain.DeactivateAsync();
        await first.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, first.GrainDisposals);
        Assert.Equal(1, state.Disposals);
        Assert.Equal(42, await grain.GetValueAsync());
        var observations = Probe.Get(grain.GetGrainId());
        Assert.Equal(2, observations.Length);
        var recovered = observations[1];
        var recoveredState = recovered.Context.ActivationServices.GetRequiredService<BootstrapState>();
        AssertComposition(recovered, recoveredState, grainClass, expectedActivationValue: 42);
        Assert.NotSame(first.Context, recovered.Context);
        Assert.NotSame(first.Inbox, recovered.Inbox);
        Assert.NotSame(first.Outbox, recovered.Outbox);
        Assert.Same(setup, GetSetup(recovered.Context));
        Assert.Equal(output.Envelope.MessageId, Assert.Single(recovered.Outbox!.Messages()).Envelope.MessageId);
        Assert.Equal(1, GetProcessedCount(recovered));
        Assert.Equal(DeliveryStatus.Duplicate, (await grain.AsReference<IInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        Assert.Equal(0, recoveredState.HandlerCalls);
        Assert.Equal(2, Fixture.Storage.GetReadCount(journal));

        var delivered = Delivery.WaitForOutputAsync(grain.GetGrainId());
        var drained = Delivery.WaitForDrainAsync(grain.GetGrainId());
        Delivery.Release();
        var receipt = await delivered;
        await drained;
        Assert.Equal(output.Envelope.MessageId, receipt.MessageId);
        Assert.Equal(42, receipt.Value);
        Assert.Empty(recovered.Outbox!.Messages());
        var sink = Fixture.Client.GetGrain<IBootstrapOutputGrain>("capture");
        Assert.Equal(1, await sink.GetMessageCountAsync());
        Assert.Equal(DeliveryStatus.Duplicate, (await sink.AsReference<IInboxExtension>().DeliverAsync(output.ToInboxMessage(), Cancellation)).Status);
        Assert.Equal(1, await sink.GetMessageCountAsync());
    }

    [Fact]
    public async Task OrdinarySend_SchedulesWakeupBeforeCaptureAndDispatchesAfterAck()
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        using var scheduling = Fixture.JobManagerProbe.BlockNext(MessagingPump.JobName);
        var operation = grain.SendValueAsync(42);
        await scheduling.WaitUntilEnteredAsync();
        Assert.Equal(42, observation.Value!.Value);
        Assert.Equal(1, observation.Outbox!.Count());
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        var storage = Fixture.Storage.BlockWrite(journal);
        var delivered = Delivery.WaitForOutputAsync(grain.GetGrainId());
        Delivery.Release();
        scheduling.Continue();
        await storage.WaitUntilEnteredAsync();
        Assert.Single(observation.Outbox!.Messages());
        Assert.False(delivered.IsCompleted);
        Assert.Equal(1, Fixture.JobManagerProbe.GetSuccessCount(MessagingPump.JobName, grain.GetGrainId()));
        storage.Release();
        await operation;
        var receipt = await delivered;
        Assert.Equal(42, receipt.Value);
        Assert.Equal(42, await grain.GetValueAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinarySend_SchedulingFailurePreservesPendingBusinessForExplicitRetry(bool ambiguous)
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        if (ambiguous) Fixture.JobManagerProbe.FailAfterNext(MessagingPump.JobName);
        else Fixture.JobManagerProbe.FailNext(MessagingPump.JobName);
        var prerequisite = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => grain.SendValueAsync(42));
        Assert.IsType<IOException>(prerequisite.InnerException);
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(1, observation.Outbox!.Count());
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(ambiguous ? 1 : 0, Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grain.GetGrainId()).Count);
        await grain.PersistAsync();
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(42, TestApplicationProtocol.Read(Sessions, Assert.Single(observation.Outbox!.Messages())).Body);
        Assert.NotEmpty(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grain.GetGrainId()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinarySend_FreshReplayResolvesActualWriteOutcome(bool committed)
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        if (committed) Fixture.Storage.FailAfterWrite(journal);
        else Fixture.Storage.FailWrite(journal);
        await Assert.ThrowsAsync<IOException>(() => grain.SendValueAsync(42));
        Assert.Equal(writes + (committed ? 1 : 0), Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grain.GetGrainId()));
        await grain.DeactivateAsync();
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(committed ? 42 : 11, await grain.GetValueAsync());
        var recovered = Probe.Get(grain.GetGrainId())[^1];
        Assert.NotSame(observation.Context, recovered.Context);
        Assert.Equal(committed ? 1 : 0, recovered.Outbox!.Count());
        if (committed)
        {
            Assert.Equal(42, TestApplicationProtocol.Read(Sessions, Assert.Single(recovered.Outbox!.Messages())).Body);
        }
        else
        {
            Assert.Empty(recovered.Outbox!.Messages());
        }
    }

    [Fact]
    public async Task SelectedWithoutConstructorDependencies_MaterializesMessagingStateBeforeRead()
    {
        var grain = Control<EagerBootstrapGrain>();
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var read = Fixture.Storage.BlockRead(journal);
        var activation = grain.PingAsync();
        try
        {
            await read.WaitUntilEnteredAsync();
            var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
            Assert.Same(observation.ConstructedGrain, observation.Context.GrainInstance);
            var manager = observation.Context.ActivationServices.GetRequiredService<IJournaledStateManager>();
            Assert.Equal(2, BootstrapState.ReadMessagingStates(manager).Count());
            Assert.Equal(0, observation.Activations);
            Assert.Single(GetSetup(observation.Context).GetInvocationList());
        }
        finally
        {
            read.Release();
        }

        await activation;
        Assert.Equal(1, Fixture.Storage.GetReadCount(journal));
    }

    [Theory]
    [InlineData(typeof(UnselectedBootstrapGrain), false)]
    [InlineData(typeof(JournalOnlyBootstrapGrain), true)]
    public async Task UnselectedComposition_LeavesMessagingUnresolved(Type grainClass, bool journaled)
    {
        var grain = Fixture.Client.GetGrain<IBootstrapControlGrain>(Guid.NewGuid(), grainClass.FullName!);
        await grain.PingAsync();
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        Assert.Equal(1, observation.Activations);
        Assert.Null(observation.Inbox);
        Assert.Null(observation.Outbox);
        Assert.Null(GetSetupOrDefault(observation.Context));
        Assert.Null(observation.Context.GetComponent<MessagingShared>());
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        Assert.Equal(journaled ? 1 : 0, Fixture.Storage.GetCreationCount(journal));
        Assert.Equal(journaled ? 1 : 0, Fixture.Storage.GetReadCount(journal));
        Assert.Equal(0, Fixture.Storage.GetInitializationCount(journal));
        if (journaled)
        {
            Assert.NotNull(observation.Manager);
            Assert.True(observation.Manager.TryGetStateMachine("bootstrap-journal-only", out var state));
            Assert.Same(observation.Value, state);
            Assert.Empty(BootstrapState.ReadMessagingStates(observation.Manager));
        }
        else
        {
            Assert.Null(observation.Manager);
        }

        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Theory]
    [InlineData(typeof(ReentrantBootstrapGrain))]
    [InlineData(typeof(MayInterleaveBootstrapGrain))]
    [InlineData(typeof(AlwaysInterleaveBootstrapGrain))]
    [InlineData(typeof(MetadataReentrantBootstrapGrain))]
    [InlineData(typeof(MetadataMayInterleaveBootstrapGrain))]
    public async Task ReentrantAndInterleavingModels_AreAcceptedAndBindTheInbox(Type grainClass)
    {
        var grain = Fixture.Client.GetGrain<IBootstrapControlGrain>(Guid.NewGuid(), grainClass.FullName!);
        await grain.PingAsync();
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        Assert.Equal(1, observation.Activations);
        var inbox = observation.Context.ActivationServices.GetRequiredService<Inbox>();
        Assert.Same(inbox, observation.Context.GetComponent<IGrainExtensionBinder>()!.GetExtension<IInboxExtension>());

        // No handler is registered by control grains, so the installed extension reports that explicitly.
        var result = await grain.AsReference<IInboxExtension>().DeliverAsync(CreateEnvelope(grain), Cancellation);
        Assert.Equal(DeliveryStatus.HandlerNotFound, result.Status);
        Assert.False(observation.Context.Deactivated.IsCompleted);
    }

    [Fact]
    public async Task StatelessWorker_FailsBeforeStorageAndCleansScope()
    {
        var grain = Control<StatelessBootstrapGrain>();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.PingAsync());
        Assert.Contains("one activation", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(nameof(StatelessBootstrapGrain), exception.ToString(), StringComparison.Ordinal);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.False(observation.InstanceAvailableInConstructor);
        Assert.NotNull(observation.ConstructedGrain);
        Assert.Equal(0, observation.Activations);
        Assert.Equal(1, observation.GrainDisposals);
        Assert.Equal(1, observation.Disposals);
        Assert.Equal(0, Fixture.Storage.GetCreationCount(JournalId.FromGrainId(grain.GetGrainId())));
        AssertNoStorageWork(grain.GetGrainId());
        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Theory]
    [InlineData(typeof(MetadataStatelessPlacementBootstrapGrain), "StatelessWorkerPlacement")]
    [InlineData(typeof(AliasedStatelessPlacementBootstrapGrain), BootstrapClusterFixture.StatelessPlacementAlias)]
    public async Task ResolvedStatelessPlacement_FailsBeforeStorageAndCleansScope(Type grainClass, string placementKey)
    {
        Assert.False(grainClass.IsDefined(typeof(StatelessWorkerAttribute), inherit: true));
        var grain = Fixture.Client.GetGrain<IBootstrapControlGrain>(Guid.NewGuid(), grainClass.FullName!);
        var services = Fixture.Cluster.Silos[0].ServiceProvider;
        var properties = services.GetRequiredService<GrainPropertiesResolver>().GetGrainProperties(grain.GetGrainId().Type);
        Assert.Equal(placementKey, properties.Properties[WellKnownGrainTypeProperties.PlacementStrategy]);
        var placement = services.GetRequiredService<PlacementStrategyResolver>().GetPlacementStrategy(grain.GetGrainId().Type);
        Assert.Equal(new StatelessWorkerAttribute().PlacementStrategy.GetType(), placement.GetType());
        if (placementKey == BootstrapClusterFixture.StatelessPlacementAlias)
        {
            Assert.Same(services.GetRequiredKeyedService<PlacementStrategy>(placementKey), placement);
        }

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.PingAsync());
        Assert.Contains("one activation", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(grainClass.Name, exception.ToString(), StringComparison.Ordinal);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.IsType(grainClass, observation.ConstructedGrain);
        Assert.Equal(0, observation.Activations);
        Assert.Equal(1, observation.GrainDisposals);
        Assert.Equal(1, observation.Disposals);
        Assert.Equal(0, Fixture.Storage.GetCreationCount(JournalId.FromGrainId(grain.GetGrainId())));
        AssertNoStorageWork(grain.GetGrainId());
        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Theory]
    [InlineData(typeof(MetadataOrdinaryPlacementBootstrapGrain), nameof(RandomPlacement))]
    [InlineData(typeof(AliasedOrdinaryPlacementBootstrapGrain), BootstrapClusterFixture.OrdinaryPlacementAlias)]
    public async Task ResolvedOrdinaryPlacement_WithoutMessagingConstructorDependencies_InitializesNormally(Type grainClass, string placementKey)
    {
        var grain = Fixture.Client.GetGrain<IBootstrapControlGrain>(Guid.NewGuid(), grainClass.FullName!);
        var services = Fixture.Cluster.Silos[0].ServiceProvider;
        var properties = services.GetRequiredService<GrainPropertiesResolver>().GetGrainProperties(grain.GetGrainId().Type);
        Assert.Equal(placementKey, properties.Properties[WellKnownGrainTypeProperties.PlacementStrategy]);
        Assert.IsType<RandomPlacement>(services.GetRequiredService<PlacementStrategyResolver>().GetPlacementStrategy(grain.GetGrainId().Type));

        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var read = Fixture.Storage.BlockRead(journal);
        var activation = grain.PingAsync();
        BootstrapObservation observation;
        try
        {
            await read.WaitUntilEnteredAsync();
            observation = Assert.Single(Probe.Get(grain.GetGrainId()));
            Assert.IsType(grainClass, observation.ConstructedGrain);
            Assert.Equal(0, observation.Activations);
            var activationServices = observation.Context.ActivationServices;
            var manager = activationServices.GetRequiredService<IJournaledStateManager>();
            Assert.True(manager.TryGetStateMachine(MessagingStateNames.Inbox, out var inbox));
            Assert.Same(activationServices.GetRequiredService<Inbox>(), inbox);
            Assert.True(manager.TryGetStateMachine(MessagingStateNames.Outbox, out var outbox));
            Assert.Same(activationServices.GetRequiredService<IOutbox>(), outbox);
            Assert.Single(GetSetup(observation.Context).GetInvocationList());
        }
        finally
        {
            read.Release();
        }

        await activation;
        Assert.Equal(1, observation.Activations);
        Assert.Equal(1, Fixture.Storage.GetCreationCount(journal));
        Assert.Equal(1, Fixture.Storage.GetReadCount(journal));
        Assert.Equal(0, Fixture.Storage.GetSuccessfulWriteCount(journal));
        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Fact]
    public async Task SetupFailure_DisposesScopeAndPreservesOriginalError()
    {
        var grain = Control<FailingBootstrapGrain>();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.PingAsync());
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        Assert.IsType<IOException>(observation.ExpectedFailure);
        Assert.Contains(observation.ExpectedFailure.Message, exception.ToString(), StringComparison.Ordinal);
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(0, observation.Activations);
        Assert.Equal(1, observation.GrainDisposals);
        Assert.Equal(1, observation.Disposals);
        Assert.NotNull(observation.Inbox);
        Assert.NotNull(observation.Outbox);
        AssertNoStorageWork(grain.GetGrainId());
        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Fact]
    public async Task UnselectedEndpointDependency_FailsExplicitly()
    {
        var grain = Control<UnselectedEndpointBootstrapGrain>();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.PingAsync());
        Assert.Contains("Messaging is not configured for grain type", exception.ToString(), StringComparison.Ordinal);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(1, observation.Disposals);
        Assert.Null(observation.Inbox);
        Assert.Null(GetSetupOrDefault(observation.Context));
        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Fact]
    public void RepeatedRegistration_InstallsOneTypeConfigurator()
    {
        var services = new ServiceCollection();
        var silo = Substitute.For<ISiloBuilder>();
        silo.Services.Returns(services);
        silo.AddJournaling();
        services.AddMessaging();
        services.AddMessaging();
        var descriptor = Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IConfigureGrainTypeComponents)
            && descriptor.ImplementationType == typeof(MessagingGrainTypeConfigurator));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(MessagingPump));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(Inbox));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(Outbox));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IOutbox));
        Assert.DoesNotContain(services, static descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(IOutbox));
        var extension = Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IGrainExtension));
        Assert.Equal(typeof(Orleans.DurableJobs.DurableJob).Assembly, Assert.IsAssignableFrom<Type>(extension.ServiceKey).Assembly);
        Assert.Empty(typeof(IMessagingGrain).GetInterfaces());
        Assert.Empty(typeof(IMessagingGrain).GetMethods());
        Assert.False(typeof(IAddressable).IsAssignableFrom(typeof(IMessagingGrain)));
    }

    private static void AssertComposition(BootstrapObservation observation, BootstrapState state, Type grainClass, int expectedActivationValue)
    {
        Assert.Equal(grainClass, observation.ConstructedGrain!.GetType());
        Assert.False(observation.InstanceAvailableInConstructor);
        Assert.Same(observation.ConstructedGrain, state.GrainAtActivation);
        Assert.Equal(2, state.MessagingStateCountAtActivation);
        Assert.Equal(1, observation.Activations);
        Assert.Equal(expectedActivationValue, state.ActivationValue);
        var services = observation.Context.ActivationServices;
        Assert.Same(observation.Manager, services.GetRequiredService<IJournaledStateManager>());
        var applicationManager = services.GetRequiredService<IDurableStateManager>();
        Assert.Same(observation.Manager, applicationManager);
        Assert.Same(observation.Value, applicationManager.GetOrAddState<IDurableValue<int>>("bootstrap-value"));
        var inbox = services.GetRequiredService<Inbox>();
        var outbox = services.GetRequiredService<Outbox>();
        Assert.Same(inbox, observation.Inbox);
        Assert.Same(outbox, observation.Outbox);
        Assert.True(observation.Manager!.TryGetStateMachine(MessagingStateNames.Inbox, out var inboxState));
        Assert.Same(inbox, inboxState);
        Assert.True(observation.Manager.TryGetStateMachine(MessagingStateNames.Outbox, out var outboxState));
        Assert.Same(outbox, outboxState);
        Assert.Same(inbox, observation.Context.GetComponent<IGrainExtensionBinder>()!.GetExtension<IInboxExtension>());
        Assert.Same(state, services.GetRequiredService<BootstrapState>());
    }

    private IBootstrapTestGrain CreateGrain(Type grainClass) => grainClass == typeof(GenericBootstrapGrain<int>)
        ? Fixture.Client.GetGrain<IGenericBootstrapTestGrain<int>>(Guid.NewGuid())
        : Fixture.Client.GetGrain<IBootstrapTestGrain>(Guid.NewGuid(), grainClass.FullName!);

    private IBootstrapControlGrain Control<T>() => Fixture.Client.GetGrain<IBootstrapControlGrain>(Guid.NewGuid(), typeof(T).FullName!);

    private int _commandSequence;

    private InboxMessage CreateEnvelope(IAddressable grain) =>
        OutboxTestMessages.CreateApplication(Fixture.Client.ServiceProvider,
            MessageId.Create("test", grain.GetGrainId().ToString(), "command", (++_commandSequence).ToString(CultureInfo.InvariantCulture)),
            GrainId.Create("bootstrap-sender", "external"), grain.GetGrainId(), BootstrapState.Route, 1).ToInboxMessage();

    private static int GetProcessedCount(BootstrapObservation observation) =>
        InboxTestAccess.GetProcessedCount(Assert.IsType<Inbox>(observation.Inbox));

    private static Delegate GetSetup(IGrainContext context) => Assert.IsAssignableFrom<Delegate>(GetSetupOrDefault(context));

    private static object? GetSetupOrDefault(IGrainContext context)
    {
        var shared = context.GetType().GetField("_shared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        return shared.GetType().GetField("_activationSetup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shared);
    }

    private void AssertNoStorageWork(GrainId grainId)
    {
        var journal = JournalId.FromGrainId(grainId);
        Assert.Equal(0, Fixture.Storage.GetInitializationCount(journal));
        Assert.Equal(0, Fixture.Storage.GetReadCount(journal));
        Assert.Equal(0, Fixture.Storage.GetSuccessfulWriteCount(journal));
    }

    private void AssertNoScheduledJobs(GrainId grainId) =>
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, grainId));
}
