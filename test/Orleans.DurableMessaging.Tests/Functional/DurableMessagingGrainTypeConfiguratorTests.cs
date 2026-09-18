using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingGrainTypeConfiguratorTests() : DurableMessagingBehaviorTestBase(new BootstrapClusterFixture())
{
    private BootstrapDeliveryProbe Delivery => ((BootstrapClusterFixture)Fixture).Delivery;
    private BootstrapProbe Probe => ((BootstrapClusterFixture)Fixture).Probe;
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(PlainBootstrapGrain))]
    [InlineData(typeof(ApplicationBootstrapGrain))]
    [InlineData(typeof(InterfaceBootstrapGrain))]
    [InlineData(typeof(GenericBootstrapGrain<int>))]
    [InlineData(typeof(LegacyBootstrapGrain))]
    [InlineData(typeof(LegacyMarkedBootstrapGrain))]
    public async Task SelectedComposition_BindsOnceAndReplaysFreshScopedState(Type grainClass)
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
        Assert.Equal(DeliveryStatus.Accepted, (await grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        await handler.WaitUntilEnteredAsync();
        using var preparation = Fixture.JobManagerProbe.BlockNext(BootstrapOutboxServices.JobName);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        handler.Release();
        await preparation.WaitUntilEnteredAsync();
        Assert.Equal(42, first.Value!.Value);
        Assert.Equal(0, first.Inbox!.Count);
        Assert.Equal(1, first.Outbox!.Count);
        Assert.Single(GetProcessed(first.Context));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, Fixture.JobManagerProbe.GetSuccessCount(BootstrapOutboxServices.JobName, grain.GetGrainId()));
        preparation.Continue();
        await state.Handled.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(0, first.Inbox.Count);
        Assert.Single(GetProcessed(first.Context));
        var output = Assert.Single(first.Outbox.Messages);
        var scheduled = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(BootstrapOutboxServices.JobName, grain.GetGrainId()));
        var owner = first.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<DurableJob>>("__orleans.durable-messaging.outbox-job-handle").Value;
        Assert.Equal(scheduled.Id, owner!.Id);
        Assert.Equal(scheduled.ShardId, owner.ShardId);
        Assert.Equal(1, state.HandlerCalls);
        Assert.Equal(DeliveryStatus.Duplicate, (await grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        Assert.Equal(1, state.HandlerCalls);

        var other = CreateGrain(grainClass);
        Assert.Equal(0, await other.GetValueAsync());
        var isolated = Assert.Single(Probe.Get(other.GetGrainId()));
        Assert.NotSame(first.Manager, isolated.Manager);
        Assert.NotSame(first.Inbox, isolated.Inbox);
        Assert.NotSame(first.Outbox, isolated.Outbox);
        Assert.Empty(isolated.Outbox!.Messages);
        Assert.Equal(42, await grain.GetValueAsync());
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
        Assert.NotSame(first.Manager, recovered.Manager);
        Assert.NotSame(first.Value, recovered.Value);
        Assert.NotSame(first.Inbox, recovered.Inbox);
        Assert.NotSame(first.Outbox, recovered.Outbox);
        Assert.Same(setup, GetSetup(recovered.Context));
        Assert.Equal(output.MessageId, Assert.Single(recovered.Outbox!.Messages).MessageId);
        Assert.Single(GetProcessed(recovered.Context));
        Assert.Equal(DeliveryStatus.Duplicate, (await grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        Assert.Equal(0, recoveredState.HandlerCalls);
        Assert.Equal(2, Fixture.Storage.GetReadCount(journal));

        var delivered = Delivery.WaitForOutputAsync(grain.GetGrainId());
        Delivery.Release();
        var receipt = await delivered;
        await recoveredState.OutboxDrained.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(output.MessageId, receipt.MessageId);
        Assert.Equal(42, receipt.Value);
        Assert.Empty(recovered.Outbox.Messages);
        var sink = Fixture.Client.GetGrain<IBootstrapOutputGrain>("capture");
        Assert.Equal(1, await sink.GetMessageCountAsync());
        Assert.Equal(DeliveryStatus.Duplicate, (await sink.AsReference<IDurableInboxExtension>().DeliverAsync(output, Cancellation)).Status);
        Assert.Equal(1, await sink.GetMessageCountAsync());
    }

    [Fact]
    public async Task SelectedWithoutConstructorDependencies_MaterializesPrimaryStateBeforeRead()
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
            Assert.Single(BootstrapState.ReadMessagingStates(manager), static observer => observer.GetType().Name == "InboxJournalState");
            Assert.True(manager.TryGetStateMachine("__orleans.durable-messaging.inbox", out _));
            Assert.True(manager.TryGetStateMachine(BootstrapOutboxServices.StateName, out _));
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
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        Assert.Equal(journaled ? 1 : 0, Fixture.Storage.GetCreationCount(journal));
        Assert.Equal(journaled ? 1 : 0, Fixture.Storage.GetReadCount(journal));
        Assert.Equal(0, Fixture.Storage.GetInitializationCount(journal));
        if (journaled)
        {
            Assert.NotNull(observation.Manager);
            Assert.True(observation.Manager.TryGetStateMachine("bootstrap-journal-only", out var state));
            Assert.Same(observation.Value, state);
            Assert.False(observation.Manager.TryGetStateMachine("__orleans.durable-messaging.inbox", out _));
            Assert.False(observation.Manager.TryGetStateMachine(BootstrapOutboxServices.StateName, out _));
            Assert.Empty(BootstrapState.ReadMessagingStates(observation.Manager));
        }
        else
        {
            Assert.Null(observation.Manager);
        }
        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Theory]
    [InlineData(typeof(ReentrantBootstrapGrain), "non-reentrant")]
    [InlineData(typeof(StatelessBootstrapGrain), "one activation")]
    [InlineData(typeof(MayInterleaveBootstrapGrain), "non-reentrant")]
    [InlineData(typeof(AlwaysInterleaveBootstrapGrain), "interleavable method")]
    [InlineData(typeof(MetadataReentrantBootstrapGrain), "non-reentrant")]
    [InlineData(typeof(MetadataMayInterleaveBootstrapGrain), "non-reentrant")]
    public async Task UnsupportedMarkedModel_FailsBeforeStorageAndCleansScope(Type grainClass, string diagnostic)
    {
        var grain = Fixture.Client.GetGrain<IBootstrapControlGrain>(Guid.NewGuid(), grainClass.FullName!);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.PingAsync());
        Assert.Contains(diagnostic, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(grainClass.Name, exception.ToString(), StringComparison.Ordinal);
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
        var extension = Assert.IsAssignableFrom<IDisposable>(observation.Extension);
        var shutdown = (CancellationTokenSource)extension.GetType().GetField("_shutdownCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!;
        Assert.Throws<ObjectDisposedException>(() => shutdown.Token);
        extension.Dispose();
        Assert.Equal(1, Fixture.Storage.GetCreationCount(JournalId.FromGrainId(grain.GetGrainId())));
        AssertNoStorageWork(grain.GetGrainId());
        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Fact]
    public async Task UnselectedEndpointDependency_FailsExplicitly()
    {
        var grain = Control<UnselectedEndpointBootstrapGrain>();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.PingAsync());
        Assert.Contains("Durable inbox activation requires IDurableMessagingGrain or DurableGrain", exception.ToString(), StringComparison.Ordinal);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(1, observation.Disposals);
        Assert.Equal(1, observation.GrainDisposals);
        Assert.Null(GetSetupOrDefault(observation.Context));
        AssertNoScheduledJobs(grain.GetGrainId());
    }

    [Fact]
    public void RepeatedRegistration_InstallsOneTypeConfigurator()
    {
        var services = new ServiceCollection();
        ReceiverTestServices.Add(services, static _ => { });
        BootstrapOutboxServices.Add(services);
        ReceiverTestServices.Add(services, static _ => { });
        BootstrapOutboxServices.Add(services);
        var descriptor = Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IConfigureGrainTypeComponents));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(ReceiverTestServices.GetImplementationType("DurableMessagingGrainTypeConfigurator"), descriptor.ImplementationType);
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IDurableOutbox));
        foreach (var stateName in BootstrapOutboxServices.StateNames)
        {
            Assert.Single(services, descriptor => descriptor.IsKeyedService && Equals(descriptor.ServiceKey, stateName));
        }
        Assert.Empty(typeof(IDurableMessagingGrain).GetInterfaces());
        Assert.Empty(typeof(IDurableMessagingGrain).GetMethods());
        Assert.False(typeof(IAddressable).IsAssignableFrom(typeof(IDurableMessagingGrain)));
    }

    private static void AssertComposition(BootstrapObservation observation, BootstrapState state, Type grainClass, int expectedActivationValue)
    {
        Assert.Equal(grainClass, observation.ConstructedGrain!.GetType());
        Assert.False(observation.InstanceAvailableInConstructor);
        Assert.Same(observation.ConstructedGrain, state.GrainAtRecovery);
        Assert.Equal(1, state.RecoveryStarts);
        Assert.Equal(1, state.RecoveryCompletions);
        Assert.Equal(1, state.PrimaryStateCountAtRecovery);
        Assert.Equal(1, observation.Activations);
        Assert.Equal(expectedActivationValue, state.ActivationValue);
        var services = observation.Context.ActivationServices;
        Assert.Same(observation.Manager, services.GetRequiredService<IJournaledStateManager>());
        var applicationManager = services.GetRequiredService<IDurableStateManager>();
        Assert.Same(observation.Manager, applicationManager);
        Assert.Same(observation.Value, applicationManager.GetOrAddState<IDurableValue<int>>("bootstrap-value"));
        Assert.True(applicationManager.TryGetState<IDurableOutbox>("test-handler-output", out var applicationOutbox));
        Assert.Same(observation.Outbox, applicationOutbox);
        Assert.Same(observation.Outbox, applicationManager.GetOrAddState<IDurableOutbox>("test-handler-output"));
        Assert.Same(observation.Value, services.GetRequiredKeyedService<IDurableValue<int>>("bootstrap-value"));
        Assert.Same(observation.Inbox, services.GetRequiredService<IDurableInbox>());
        Assert.Same(observation.Outbox, services.GetRequiredService<IDurableOutbox>());
        Assert.Equal(ReceiverTestServices.GetImplementationType("DurableOutbox"), observation.Outbox!.GetType());
        Assert.True(observation.Inbox!.TryGetHandler(BootstrapState.Route, out var handler));
        Assert.Same(state, handler);
        var primary = Assert.Single(BootstrapState.ReadMessagingStates(observation.Manager!), static state => state.GetType().Name == "InboxJournalState");
        Assert.Same(services.GetRequiredService(ReceiverTestServices.GetImplementationType("InboxJournalState")), primary);
        Assert.Same(primary, applicationManager.GetOrAddState<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("__orleans.durable-messaging.inbox"));
        Assert.Same(observation.Outbox, services.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableOutbox")));
        foreach (var name in BootstrapOutboxServices.StateNames)
        {
            Assert.True(observation.Manager!.TryGetStateMachine(name, out _));
        }
        Assert.True(observation.Manager!.TryGetStateMachine("__orleans.durable-messaging.outbox-job-handle", out var jobState));
        Assert.Same(jobState, services.GetRequiredKeyedService<IDurableValue<DurableJob>>("__orleans.durable-messaging.outbox-job-handle"));
    }
    private IBootstrapTestGrain CreateGrain(Type grainClass) => grainClass == typeof(GenericBootstrapGrain<int>)
        ? Fixture.Client.GetGrain<IGenericBootstrapTestGrain<int>>(Guid.NewGuid())
        : Fixture.Client.GetGrain<IBootstrapTestGrain>(Guid.NewGuid(), grainClass.FullName!);
    private IBootstrapControlGrain Control<T>() => Fixture.Client.GetGrain<IBootstrapControlGrain>(Guid.NewGuid(), typeof(T).FullName!);
    private DurableEnvelope CreateEnvelope(IBootstrapTestGrain grain) =>
        new DurableEnvelopeBuilder(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(), GrainId.Create("bootstrap-sender", "external"))
            .To(grain.GetGrainId(), BootstrapState.Route).WithBody(1).Build();
    private static IDurableDictionary<(GrainId, Guid), DateTimeOffset> GetProcessed(IGrainContext context) =>
        context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("__orleans.durable-messaging.inbox-processed");
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
    private void AssertNoScheduledJobs(GrainId grainId)
    {
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, grainId));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(BootstrapOutboxServices.JobName, grainId));
    }
}
