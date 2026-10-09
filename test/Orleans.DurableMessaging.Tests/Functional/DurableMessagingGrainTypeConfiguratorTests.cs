using System.Reflection;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Concurrency;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
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
    [InlineData(typeof(DurableBootstrapGrain))]
    [InlineData(typeof(MarkedDurableBootstrapGrain))]
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
        using var envelope = CreateEnvelope(grain);
        Assert.Equal(DeliveryStatus.Accepted, (await grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        await handler.WaitUntilEnteredAsync();
        Assert.Equal(41, first.Value!.Value);
        Assert.Equal(1, first.Inbox!.Count);
        Assert.Equal(0, first.Outbox!.Count);
        Assert.Empty(GetProcessed(first.Context));
        Assert.Equal(0, state.HandlerCalls);
        using var preparation = Fixture.JobManagerProbe.BlockNext(BootstrapOutboxServices.JobName);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        handler.Release();
        await preparation.WaitUntilEnteredAsync();
        Assert.Equal(42, first.Value!.Value);
        Assert.Equal(0, first.Inbox!.Count);
        Assert.Equal(1, first.Outbox!.Count);
        Assert.Single(GetProcessed(first.Context));
        Assert.Equal(1, state.HandlerCalls);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, Fixture.JobManagerProbe.GetSuccessCount(BootstrapOutboxServices.JobName, grain.GetGrainId()));
        var storage = Fixture.Storage.BlockWrite(journal);
        preparation.Continue();
        await storage.WaitUntilEnteredAsync();
        Assert.Equal(42, first.Value!.Value);
        Assert.Equal(0, first.Inbox!.Count);
        Assert.Equal(1, first.Outbox!.Count);
        Assert.Single(GetProcessed(first.Context));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        storage.Release();
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(0, first.Inbox.Count);
        Assert.Single(GetProcessed(first.Context));
        using var output = Assert.Single(first.Outbox.Messages).Retain();
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
        var drained = Delivery.WaitForDrainAsync(grain.GetGrainId());
        Delivery.Release();
        var receipt = await delivered;
        await drained;
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(output.MessageId, receipt.MessageId);
        Assert.Equal(42, receipt.Value);
        Assert.Empty(recovered.Outbox.Messages);
        var sink = Fixture.Client.GetGrain<IBootstrapOutputGrain>("capture");
        Assert.Equal(1, await sink.GetMessageCountAsync());
        Assert.Equal(DeliveryStatus.Duplicate, (await sink.AsReference<IDurableInboxExtension>().DeliverAsync(output, Cancellation)).Status);
        Assert.Equal(1, await sink.GetMessageCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousEnvelopeSend_SchedulesBeforeCaptureAndDispatchesAfterAck(bool handlerSend)
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        using var scheduling = Fixture.JobManagerProbe.BlockNext(BootstrapOutboxServices.JobName);
        using var handler = new SynchronousHandler(observation);
        Task operation;
        if (handlerSend)
        {
            await OnOwnerTurnAsync(observation.Context, () =>
            {
                observation.Context.ActivationServices.GetRequiredService<BootstrapState>().HandlerOverride = handler;
                return Task.CompletedTask;
            });
            using var input = CreateEnvelope(grain);
            operation = grain.AsReference<IDurableInboxExtension>().DeliverAsync(input, Cancellation).AsTask();
        }
        else operation = grain.SendSynchronousValueAsync(42);
        await scheduling.WaitUntilEnteredAsync();
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Assert.Equal(42, observation.Value!.Value);
        Assert.Single(observation.Outbox!.Messages);
        Assert.Equal(handlerSend ? 1 : 0, handler.Applied);
        if (handlerSend)
        {
            Assert.Equal(0, observation.Inbox!.Count);
            Assert.Single(GetProcessed(observation.Context));
        }
        using var storage = Fixture.Storage.BlockWrite(journal);
        var delivered = Delivery.WaitForOutputAsync(grain.GetGrainId());
        Delivery.Release();
        scheduling.Continue();
        await storage.WaitUntilEnteredAsync();
        Assert.False(delivered.IsCompleted);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        storage.Release();
        await operation;
        var receipt = await delivered;
        Assert.Equal(42, receipt.Value);
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(1, Fixture.JobManagerProbe.GetSuccessCount(BootstrapOutboxServices.JobName, grain.GetGrainId()));
        if (handlerSend)
        {
            await OnOwnerTurnAsync(observation.Context, () =>
            {
                Assert.Throws<InvalidOperationException>(() => handler.Context!.Complete());
                return Task.CompletedTask;
            });
            Assert.Equal(1, handler.Applied);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousEnvelopeSend_SchedulingFailureExplicitlyRetriesPendingBusinessState(bool ambiguous)
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        if (ambiguous) Fixture.JobManagerProbe.FailAfterNext(BootstrapOutboxServices.JobName);
        else Fixture.JobManagerProbe.FailNext(BootstrapOutboxServices.JobName);
        var error = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => grain.SendSynchronousValueAsync(42));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(42, await grain.GetValueAsync());
        var pending = Assert.Single(observation.Outbox!.Messages);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.False(observation.Context.Deactivated.IsCompleted);
        await grain.PersistAsync();
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(pending.MessageId, Assert.Single(observation.Outbox.Messages).MessageId);
        Assert.Equal(2, Fixture.JobManagerProbe.GetAttemptCount(BootstrapOutboxServices.JobName, grain.GetGrainId()));
        await grain.DeactivateAsync();
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(42, await grain.GetValueAsync());
        var recovered = Probe.Get(grain.GetGrainId())[^1];
        Assert.Equal(pending.MessageId, Assert.Single(recovered.Outbox!.Messages).MessageId);
    }

    [Fact]
    public async Task SynchronousHandler_PreCommitFailureRetiresPendingEffectsAndFreshReplayAppliesOnce()
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        using var handler = new SynchronousHandler(observation);
        await OnOwnerTurnAsync(observation.Context, () =>
        {
            observation.Context.ActivationServices.GetRequiredService<BootstrapState>().HandlerOverride = handler;
            return Task.CompletedTask;
        });
        Fixture.JobManagerProbe.FailNext(BootstrapOutboxServices.JobName);
        using var input = CreateEnvelope(grain);
        Assert.Equal(DeliveryStatus.Accepted, (await grain.AsReference<IDurableInboxExtension>().DeliverAsync(input, Cancellation)).Status);
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(1, handler.Applied);
        Assert.Equal(42, observation.Value!.Value);
        Assert.Empty(observation.Outbox!.Messages); // Owning state was released during scope teardown.
        using var recovery = Fixture.HandlerProbe.Arm(grain.GetGrainId(), BootstrapState.Route);
        var activation = grain.GetValueAsync();
        await recovery.WaitUntilEnteredAsync();
        var recovered = Probe.Get(grain.GetGrainId())[^1];
        Assert.NotSame(observation.Context, recovered.Context);
        Assert.Equal(11, recovered.Value!.Value);
        Assert.Empty(recovered.Outbox!.Messages);
        using var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(grain.GetGrainId()));
        recovery.Release();
        await storage.WaitUntilEnteredAsync();
        Assert.Equal(12, recovered.Value!.Value);
        Assert.Single(recovered.Outbox.Messages);
        Assert.Single(GetProcessed(recovered.Context));
        storage.Release();
        await activation;
        Assert.Equal(12, await grain.GetValueAsync());
        Assert.Equal(1, recovered.Context.ActivationServices.GetRequiredService<BootstrapState>().HandlerCalls);
        Assert.Equal(1, handler.Applied);
    }

    [Fact]
    public async Task SynchronousHandler_PreCommitAcceptanceFailureRetiresOwnerBeforeFreshAcceptance()
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        await OnOwnerTurnAsync(observation.Context, () =>
        {
            observation.Manager!.Hooks.Add(new JournaledStateHook
            {
                BeforeOperation = (_, _) => throw new IOException("Acceptance prerequisite failed.")
            });
            return Task.CompletedTask;
        });
        using var envelope = CreateEnvelope(grain);
        var error = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() =>
            grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation).AsTask());
        Assert.IsType<IOException>(error.InnerException);
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, observation.Inbox!.Count);
        Assert.Equal(11, await grain.GetValueAsync());
        var recovered = Probe.Get(grain.GetGrainId())[^1];
        Assert.NotSame(observation.Context, recovered.Context);
        Assert.Empty(recovered.Outbox!.Messages);
        Assert.Equal(0, recovered.Inbox!.Count);
        using var handler = Fixture.HandlerProbe.Arm(grain.GetGrainId(), BootstrapState.Route);
        Assert.Equal(DeliveryStatus.Accepted, (await grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        await handler.WaitUntilEnteredAsync();
        using var storage = Fixture.Storage.BlockWrite(journal);
        handler.Release();
        await storage.WaitUntilEnteredAsync();
        Assert.Equal(12, recovered.Value!.Value);
        Assert.Single(GetProcessed(recovered.Context));
        storage.Release();
        Assert.Equal(12, await grain.GetValueAsync());
        Assert.Equal(1, recovered.Context.ActivationServices.GetRequiredService<BootstrapState>().HandlerCalls);
        Assert.Equal(DeliveryStatus.Duplicate, (await grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousHandler_PostCommitFailureKeepsAcknowledgedAccounting(bool acceptance)
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        using var handler = new SynchronousHandler(observation);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failHook = true;
        var failure = new IOException("Post-persistence hook failed.");
        var hook = new JournaledStateHook
        {
            AfterOperation = (_, _) =>
            {
                if (observation.Value!.Value == 42) handled.TrySetResult();
                if (failHook && (acceptance || observation.Value.Value == 42))
                {
                    reached.TrySetResult();
                    throw failure;
                }
            }
        };
        await OnOwnerTurnAsync(observation.Context, () =>
        {
            observation.Context.ActivationServices.GetRequiredService<BootstrapState>().HandlerOverride = handler;
            observation.Manager!.Hooks.Add(hook);
            return Task.CompletedTask;
        });
        using var envelope = CreateEnvelope(grain);
        var delivery = grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation).AsTask();
        if (acceptance)
        {
            var error = await Assert.ThrowsAsync<JournaledStatePostCommitException>(() => delivery);
            Assert.IsType<IOException>(error.InnerException);
        }
        else Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        await OnOwnerTurnAsync(observation.Context, () =>
        {
            failHook = false;
            return Task.CompletedTask;
        });
        Assert.False(observation.Context.Deactivated.IsCompleted);
        Assert.Equal(DeliveryStatus.Duplicate, (await grain.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, Cancellation)).Status);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(1, handler.Applied);
        Assert.Single(GetProcessed(observation.Context));
        Assert.Single(observation.Outbox!.Messages);
        await OnOwnerTurnAsync(observation.Context, () =>
        {
            observation.Manager!.Hooks.Remove(hook);
            return Task.CompletedTask;
        });
        await grain.PersistAsync();
        Assert.False(observation.Context.Deactivated.IsCompleted);
    }

    private sealed class SynchronousHandler(BootstrapObservation observation) : IInboxHandler, IDisposable
    {
        public IInboxHandlerContext? Context { get; private set; }
        public DurableEnvelope Output { get; private set; }
        public int Applied { get; private set; }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            Output.Dispose();
            Output = TestApplicationProtocol.Create(observation.Context.ActivationServices.GetRequiredService<SerializerSessionPool>(), observation.Context.GrainId, BootstrapState.OutputTarget, "output", 42, context.Envelope.MessageId.CreateChildKey("output"));
            Applied++;
            observation.Value!.Value = 42;
            observation.Outbox!.Send(Output);
            context.Complete();
            return ValueTask.CompletedTask;
        }
        public void Dispose() => Output.Dispose();
    }

    private static Task OnOwnerTurnAsync(IGrainContext context, Func<Task> action)
    {
        var task = new Task<Task>(action);
        context.Scheduler.QueueTask(task);
        return task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
    }

    [Fact]
    public async Task OrdinaryOpaqueSend_StagesBusinessBeforeCaptureAndDispatchesAfterAck()
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        using var scheduling = Fixture.JobManagerProbe.BlockNext(BootstrapOutboxServices.JobName);
        var operation = grain.SendValueAsync(42);
        await scheduling.WaitUntilEnteredAsync();
        Assert.Equal(42, observation.Value!.Value);
        Assert.Equal(1, observation.Outbox!.Count);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        var storage = Fixture.Storage.BlockWrite(journal);
        var delivered = Delivery.WaitForOutputAsync(grain.GetGrainId());
        Delivery.Release();
        scheduling.Continue();
        await storage.WaitUntilEnteredAsync();
        Assert.Equal(42, observation.Value.Value);
        Assert.Single(observation.Outbox.Messages);
        Assert.False(delivered.IsCompleted);
        Assert.Equal(1, Fixture.JobManagerProbe.GetSuccessCount(BootstrapOutboxServices.JobName, grain.GetGrainId()));
        storage.Release();
        await operation;
        var receipt = await delivered;
        Assert.Equal(42, receipt.Value);
        Assert.Equal(42, await grain.GetValueAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryOpaqueSend_SchedulingFailurePreservesPendingBusinessForExplicitRetry(bool ambiguous)
    {
        var grain = CreateGrain(typeof(PlainBootstrapGrain));
        await grain.SetValueAsync(11);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        if (ambiguous) Fixture.JobManagerProbe.FailAfterNext(BootstrapOutboxServices.JobName);
        else Fixture.JobManagerProbe.FailNext(BootstrapOutboxServices.JobName);
        var prerequisite = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => grain.SendValueAsync(42));
        Assert.IsType<IOException>(prerequisite.InnerException);
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(1, observation.Outbox!.Count);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(ambiguous ? 1 : 0, Fixture.JobManagerProbe.GetScheduledJobs(BootstrapOutboxServices.JobName, grain.GetGrainId()).Count);
        await grain.PersistAsync();
        Assert.Equal(42, await grain.GetValueAsync());
        Assert.Equal(42, TestApplicationProtocol.Read(observation.Context.ActivationServices.GetRequiredService<SerializerSessionPool>(), Assert.Single(observation.Outbox.Messages)).Body);
        Assert.Equal(2, Fixture.JobManagerProbe.GetAttemptCount(BootstrapOutboxServices.JobName, grain.GetGrainId()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryOpaqueSend_FreshReplayResolvesActualWriteOutcome(bool committed)
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
        var scheduled = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(BootstrapOutboxServices.JobName, grain.GetGrainId()));
        observation.Context.Deactivate(new DeactivationReason(
            DeactivationReasonCode.ApplicationRequested, "Replay the actual failed-write outcome."), Cancellation);
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(committed ? 42 : 11, await grain.GetValueAsync());
        var recovered = Probe.Get(grain.GetGrainId())[^1];
        Assert.NotSame(observation.Context, recovered.Context);
        Assert.Equal(committed ? 1 : 0, recovered.Outbox!.Count);
        var owner = recovered.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<DurableJob>>("__orleans.durable-messaging.outbox-job-handle").Value;
        if (committed)
        {
            Assert.Equal(scheduled.Id, owner!.Id);
            Assert.Equal(scheduled.ShardId, owner.ShardId);
            Assert.Equal(42, TestApplicationProtocol.Read(recovered.Context.ActivationServices.GetRequiredService<SerializerSessionPool>(), Assert.Single(recovered.Outbox.Messages)).Body);
        }
        else Assert.Null(owner);
        Assert.Equal(1, Fixture.JobManagerProbe.GetAttemptCount(BootstrapOutboxServices.JobName, grain.GetGrainId()));
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
            Assert.Equal(8, BootstrapState.ReadMessagingStates(manager).Count());
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
        Assert.False(placement.IsUsingGrainDirectory);
        if (placementKey == BootstrapClusterFixture.StatelessPlacementAlias)
        {
            Assert.Same(services.GetRequiredKeyedService<PlacementStrategy>(placementKey), placement);
        }

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.PingAsync());
        Assert.Contains("one activation", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(grainClass.Name, exception.ToString(), StringComparison.Ordinal);
        var observation = Assert.Single(Probe.Get(grain.GetGrainId()));
        await observation.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.False(observation.InstanceAvailableInConstructor);
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
        Assert.False(grainClass.IsDefined(typeof(StatelessWorkerAttribute), inherit: true));
        var grain = Fixture.Client.GetGrain<IBootstrapControlGrain>(Guid.NewGuid(), grainClass.FullName!);
        var services = Fixture.Cluster.Silos[0].ServiceProvider;
        var properties = services.GetRequiredService<GrainPropertiesResolver>().GetGrainProperties(grain.GetGrainId().Type);
        Assert.Equal(placementKey, properties.Properties[WellKnownGrainTypeProperties.PlacementStrategy]);
        var placement = services.GetRequiredService<PlacementStrategyResolver>().GetPlacementStrategy(grain.GetGrainId().Type);
        Assert.IsType<RandomPlacement>(placement);
        Assert.True(placement.IsUsingGrainDirectory);
        if (placementKey == BootstrapClusterFixture.OrdinaryPlacementAlias)
        {
            Assert.Same(services.GetRequiredKeyedService<PlacementStrategy>(placementKey), placement);
        }

        var journal = JournalId.FromGrainId(grain.GetGrainId());
        var read = Fixture.Storage.BlockRead(journal);
        var activation = grain.PingAsync();
        BootstrapObservation observation;
        try
        {
            await read.WaitUntilEnteredAsync();
            observation = Assert.Single(Probe.Get(grain.GetGrainId()));
            Assert.False(observation.InstanceAvailableInConstructor);
            Assert.IsType(grainClass, observation.ConstructedGrain);
            Assert.Same(observation.ConstructedGrain, observation.Context.GrainInstance);
            Assert.Equal(0, observation.Activations);
            var manager = observation.Context.ActivationServices.GetRequiredService<IJournaledStateManager>();
            Assert.True(manager.TryGetStateMachine("__orleans.durable-messaging.inbox", out var inbox));
            Assert.Same(observation.Context.ActivationServices.GetRequiredKeyedService<
                IDurableDictionary<HierarchicalKey, DurableEnvelope>>("__orleans.durable-messaging.inbox"), inbox);
            Assert.True(manager.TryGetStateMachine(BootstrapOutboxServices.StateName, out var outbox));
            Assert.Same(observation.Context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEnvelope>>(
                BootstrapOutboxServices.StateName), outbox);
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
        var silo = Substitute.For<ISiloBuilder>();
        silo.Services.Returns(services);
        silo.AddJournaling();
        ReceiverTestServices.Add(services, static _ => { });
        BootstrapOutboxServices.Add(services);
        ReceiverTestServices.Add(services, static _ => { });
        BootstrapOutboxServices.Add(services);
        var descriptor = Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IConfigureGrainTypeComponents)
            && descriptor.ImplementationType == ReceiverTestServices.GetImplementationType("DurableMessagingGrainTypeConfigurator"));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(ReceiverTestServices.GetImplementationType("DurableMessagingGrainTypeConfigurator"), descriptor.ImplementationType);
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IDurableOutbox));
        foreach (var stateName in BootstrapOutboxServices.StateNames.Take(6))
        {
            Assert.DoesNotContain(services, descriptor => descriptor.IsKeyedService && Equals(descriptor.ServiceKey, stateName));
        }
        Assert.Single(services, descriptor => descriptor.IsKeyedService
            && Equals(descriptor.ServiceKey, BootstrapOutboxServices.StateNames[6]));
        Assert.Single(services, descriptor => descriptor.IsKeyedService && Equals(descriptor.ServiceKey, KeyedService.AnyKey)
            && descriptor.ServiceType == typeof(IDurableDictionary<,>));
        Assert.Single(services, descriptor => descriptor.IsKeyedService && Equals(descriptor.ServiceKey, KeyedService.AnyKey)
            && descriptor.ServiceType == typeof(IDurableValue<>));
        Assert.Empty(typeof(IDurableMessagingGrain).GetInterfaces());
        Assert.Empty(typeof(IDurableMessagingGrain).GetMethods());
        Assert.False(typeof(IAddressable).IsAssignableFrom(typeof(IDurableMessagingGrain)));
    }

    private static void AssertComposition(BootstrapObservation observation, BootstrapState state, Type grainClass, int expectedActivationValue)
    {
        Assert.Equal(grainClass, observation.ConstructedGrain!.GetType());
        Assert.False(observation.InstanceAvailableInConstructor);
        Assert.Same(observation.ConstructedGrain, state.GrainAtActivation);
        Assert.Equal(8, state.MessagingStateCountAtActivation);
        Assert.Equal(1, observation.Activations);
        Assert.Equal(expectedActivationValue, state.ActivationValue);
        var services = observation.Context.ActivationServices;
        Assert.Same(observation.Manager, services.GetRequiredService<IJournaledStateManager>());
        var applicationManager = services.GetRequiredService<IDurableStateManager>();
        Assert.Same(observation.Manager, applicationManager);
        Assert.Same(observation.Value, applicationManager.GetOrAddState<IDurableValue<int>>("bootstrap-value"));
        Assert.True(applicationManager.TryGetState<IDurableDictionary<HierarchicalKey, DurableEnvelope>>(BootstrapOutboxServices.StateName, out var applicationOutbox));
        Assert.Same(services.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEnvelope>>(BootstrapOutboxServices.StateName), applicationOutbox);
        Assert.Same(applicationOutbox, applicationManager.GetOrAddState<IDurableDictionary<HierarchicalKey, DurableEnvelope>>(BootstrapOutboxServices.StateName));
        Assert.Same(observation.Value, services.GetRequiredKeyedService<IDurableValue<int>>("bootstrap-value"));
        Assert.Same(observation.Inbox, services.GetRequiredService<IDurableInbox>());
        Assert.Same(observation.Outbox, services.GetRequiredService<IDurableOutbox>());
        Assert.Equal(ReceiverTestServices.GetImplementationType("DurableOutbox"), observation.Outbox!.GetType());
        Assert.Same(state, observation.Context.ActivationServices.GetRequiredService<BootstrapState>());
        Assert.Equal(8, BootstrapState.ReadMessagingStates(observation.Manager!).Count());
        var primary = services.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEnvelope>>("__orleans.durable-messaging.inbox");
        Assert.Same(primary, applicationManager.GetOrAddState<IDurableDictionary<HierarchicalKey, DurableEnvelope>>("__orleans.durable-messaging.inbox"));
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
    private int _commandSequence;
    private DurableEnvelope CreateEnvelope(IBootstrapTestGrain grain) =>
        TestApplicationProtocol.Create(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(),
            GrainId.Create("bootstrap-sender", "external"), grain.GetGrainId(), BootstrapState.Route, 1,
            HierarchicalKey.Create("test", grain.GetGrainId().ToString(), "command", (++_commandSequence).ToString(CultureInfo.InvariantCulture)));
    private static IDurableDictionary<HierarchicalKey, DateTimeOffset> GetProcessed(IGrainContext context) =>
        context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DateTimeOffset>>("__orleans.durable-messaging.inbox-processed");
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
