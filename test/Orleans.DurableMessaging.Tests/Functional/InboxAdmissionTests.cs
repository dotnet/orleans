using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Session;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxAdmissionTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task ExplicitPreparation_AllowsPriorStateWriteThenCapturesAppliedCohortAtomically()
    {
        using var attempt = await PrepareAttemptAsync("atomic-admission");
        var state = attempt.Grain.GetSnapshotForTest();
        Assert.Equal(1, state.InboxCount);
        Assert.Equal(0, state.ProcessedMessageCount);
        Assert.Empty(state.Effects);
        Assert.Empty(attempt.Outbox);
        await OnTurnAsync(attempt.Context, () =>
            attempt.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "previous-state");
        await attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var priorCapture = attempt.Grain.Captures[^1];
        Assert.Empty(priorCapture.Effects);
        Assert.Equal(1, priorCapture.InboxCount);
        Assert.Equal(0, priorCapture.ProcessedMessageCount);
        Assert.Equal(0, priorCapture.OutboxCount);
        Assert.False(attempt.Grain.ApplyAttempted.Task.IsCompleted);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId);
        using var storage = Fixture.Storage.BlockWrite(attempt.JournalId);
        attempt.Preparation.Release();
        await storage.WaitUntilEnteredAsync();
        var queued = attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var coalesced = attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(queued.IsCompleted);
        Assert.False(coalesced.IsCompleted);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId));

        var captured = attempt.Grain.GetSnapshotForTest();
        Assert.Equal(0, captured.InboxCount);
        Assert.Equal(1, captured.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(captured.Effects).Count);
        Assert.Single(attempt.Grain.OutputCaptures[^1]);
        Assert.Equal(0, Assert.Single(attempt.Outbox.PreparedBatches).DisposeCalls);
        Assert.False(queued.IsCompleted);
        storage.Release();
        await Task.WhenAll(queued, coalesced);
        await attempt.Receiver.RequestDeactivationAsync();
        var recovered = await attempt.Receiver.GetSnapshotAsync();
        Assert.NotEqual(state.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(1, recovered.OutboxCount);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(1, Assert.Single(attempt.Outbox.PreparedBatches).DisposeCalls);
    }

    [Fact]
    public async Task HandlerStorageFailure_DeactivatesAndFailsQueuedWritesAfterPriorAcknowledgement()
    {
        using var attempt = await PrepareAttemptAsync("fault-ordering");
        var extension = attempt.Context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        await OnTurnAsync(attempt.Context, () =>
        {
            attempt.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "prior-captured";
        });
        using var storage = Fixture.Storage.BlockWrite(attempt.JournalId);
        var preceding = attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        Fixture.Storage.FailWrite(attempt.JournalId);
        var queued = attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var writes = Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId);
        attempt.Preparation.Release();
        await attempt.Grain.ApplyAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await OnTurnAsync(attempt.Context, () =>
        {
            Assert.Equal(1, attempt.Outbox.SendCalls);
            Assert.Single(attempt.Grain.GetSnapshotForTest().Effects);
        });
        Assert.False(queued.IsCompleted);
        storage.Release();
        await preceding;

        var failure = await Assert.ThrowsAsync<IOException>(() => queued);
        Assert.Same(failure, await attempt.Grain.DeactivationFailure.Task);
        await attempt.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () =>
            await ((IDurableInboxExtension)extension).DeliverAsync(attempt.Envelope, TestContext.Current.CancellationToken)));
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId));
        var failed = attempt.Grain.GetSnapshotForTest();
        Assert.Single(failed.Effects);
        Assert.Equal(0, failed.InboxCount);
        Assert.Equal(1, failed.ProcessedMessageCount);
        _ = await attempt.Receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(attempt.Receiver, 1);
        Assert.NotEqual(failed.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
    }

    [Fact]
    public async Task PhysicalOwnerInvalidationDuringPreparation_RejectsBeforeHandlerApply()
    {
        using var attempt = await PrepareAttemptAsync("owner-invalidation");
        var value = attempt.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<DurableJob>>("__orleans.durable-messaging.inbox-job-handle");
        var previous = value.Value!;
        await OnTurnAsync(attempt.Context, () => value.Value = new DurableJob
        {
            Id = previous.Id,
            ShardId = "different-physical-shard",
            Metadata = previous.Metadata,
            Name = previous.Name,
            TargetGrainId = previous.TargetGrainId,
            DueTime = previous.DueTime
        });
        var writes = Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId);
        attempt.Preparation.Release();
        var failure = Assert.IsType<InvalidOperationException>(
            await attempt.Grain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Contains("acknowledged physical job", failure.Message, StringComparison.Ordinal);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId));
        var failed = attempt.Grain.GetSnapshotForTest();
        Assert.Empty(failed.Effects);
        Assert.Equal(1, failed.InboxCount);
        Assert.Equal(0, failed.ProcessedMessageCount);
        Assert.False(attempt.Grain.ApplyAttempted.Task.IsCompleted);
        Assert.Empty(attempt.Outbox.Messages);
        await attempt.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await attempt.Receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(attempt.Receiver, 1);
        Assert.NotEqual(failed.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Empty(recovered.InboxDeadLetters);
    }

    [Fact]
    public async Task LateOutgoingIntent_AfterCapture_WaitsForNextCapture()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var durable = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEnvelope>>("test-handler-output");
        var sessions = Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var first = new DurableEnvelopeBuilder(sessions, receiver.GetGrainId()).To(receiver.GetGrainId(), "messages/output").WithBody(1).Build();
        var late = new DurableEnvelopeBuilder(sessions, receiver.GetGrainId()).To(receiver.GetGrainId(), "messages/output").WithBody(2).Build();
        await receiver.StageOutputAsync(first);
        var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var write = Fixture.WriteStateAsync(receiver).AsTask();
        await storage.WaitUntilEnteredAsync();
        await OnTurnTaskAsync(context, async () =>
        {
            using var batch = await outbox.PrepareSendAsync([late], TestContext.Current.CancellationToken);
            outbox.Send(batch);
        });
        Assert.Equal(new[] { first.MessageId }, grain.OutputCaptures[^1]);
        Assert.Equal(2, outbox.Count);
        storage.Release();
        await write;
        Assert.Equal(new[] { first.MessageId }, grain.OutputCaptures[^1]);
        await receiver.RetryWriteStateAsync();
        Assert.Equal(new[] { first.MessageId, late.MessageId }.Order(), grain.OutputCaptures[^1].Order());
        Assert.Equal(2, durable.Count);
        await receiver.RequestDeactivationAsync();
        Assert.Equal(2, (await receiver.GetSnapshotAsync()).OutboxCount);
    }

    [Fact]
    public async Task AcceptanceAfterCapture_IsAcknowledgedByLaterOperation()
    {
        var receiver = NewGrain();
        using var seed = CreateEnvelope(receiver, NewMessage(110, "seed"), "messages/cutoff");
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        using var hold = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "hold-capture-cutoff");
        var turn = receiver.HoldPumpTurnAsync("hold-capture-cutoff", replacement: null, deactivate: false);
        await hold.WaitUntilEnteredAsync();
        var extension = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        Task<DeliveryResult> initial = null!;
        await OnTurnAsync(context, () => initial = extension.DeliverAsync(seed.Value, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(DeliveryStatus.Accepted, (await initial).Status);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        grain.Captures.Clear();
        await OnTurnAsync(context, () => context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "capture-cutoff");
        var preceding = Fixture.WriteStateAsync(receiver).AsTask();
        await storage.WaitUntilEnteredAsync();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/cutoff");
        using var incoming = CreateEnvelope(receiver, NewMessage(111, "late-acceptance"), "messages/cutoff");
        Task<DeliveryResult> delivery = null!;
        await OnTurnAsync(context, () => delivery = extension.DeliverAsync(incoming.Value).AsTask());
        Assert.False(delivery.IsCompleted);
        Assert.Equal(2, grain.GetSnapshotForTest().InboxCount);
        storage.Release();
        await preceding;
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        Assert.Equal(1, grain.Captures[0].InboxCount);
        Assert.Equal(2, grain.Captures[1].InboxCount);
        Assert.Equal(2, grain.GetSnapshotForTest().InboxCount);
        hold.Release();
        await turn;
        await handler.WaitUntilEnteredAsync();
        handler.Release();
        Assert.Equal(2, (await Fixture.WaitForEffectCountAsync(receiver, 2)).ProcessedMessageCount);
    }

    [Fact]
    public async Task CallerWaitCancellation_DoesNotWithdrawCapturedAcceptance()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var storage = Fixture.Storage.BlockWrite(journal);
        using var envelope = CreateEnvelope(receiver, NewMessage(112, "cancel-wait"));
        using var cancellation = new CancellationTokenSource();
        var delivery = DeliverWithCancellationAsync(receiver, envelope.Value, cancellation.Token);
        await storage.WaitUntilEnteredAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        storage.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
    }

    [Fact]
    public async Task OwnedTimerCancellation_DuringHandlerPreparation_DeactivatesAndFreshActivationRetries()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(113, "attempt-cancel"), "messages/attempt-cancel");
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/attempt-cancel");
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var timer = Assert.Single(events.Events.Select(item => item.Payload).OfType<GrainTimerEvents.Created>(),
            item => ReferenceEquals(item.GrainContext, context)
                && item.Timer.GetType().GenericTypeArguments is [var state]
                && state.DeclaringType == ReceiverTestServices.GetImplementationType("DurableInboxExtension")).Timer;
        await OnTurnAsync(context, timer.Dispose);
        var failure = await grain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
        handler.Release();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
    }

    [Fact]
    public async Task MessagingPrimaryStates_AreTheRegisteredScopedDataInstances()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(receiver).ActivationServices;
        var manager = services.GetRequiredService<IJournaledStateManager>();
        Assert.True(manager.TryGetStateMachine("__orleans.durable-messaging.inbox", out var inbox));
        Assert.Same(services.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("__orleans.durable-messaging.inbox"), inbox);
        Assert.True(manager.TryGetStateMachine("test-handler-output", out var output));
        Assert.Same(((JournaledTestOutbox)services.GetRequiredService<IDurableOutbox>()).StoredMessages, output);
        Assert.Same(services.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEnvelope>>("test-handler-output"), output);
    }

    [Fact]
    public async Task AcceptanceWriteFailure_DeactivatesAndFreshScopeRetries()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Fixture.Storage.FailWrite(journal);
        using var envelope = CreateEnvelope(receiver, NewMessage(114, "acceptance-write-failure"));
        var failure = await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope.Value));
        Assert.Contains("Injected journal write failure", failure.Message, StringComparison.Ordinal);
        var rejected = grain.GetSnapshotForTest();
        Assert.Equal(before.ActivationId, rejected.ActivationId);
        Assert.Equal(1, rejected.InboxCount);
        Assert.NotNull(rejected.InboxJobId);
        Assert.NotNull(rejected.InboxJob);
        Assert.Empty(rejected.Effects);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        var deactivation = await grain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.IsType<IOException>(deactivation);
        Assert.Equal(failure.Message, deactivation.Message);
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(rejected.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Null(recovered.InboxJobId);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
        var jobs = Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId());
        Assert.Equal(2, jobs.Count);
        Assert.NotEqual(jobs[0].Metadata!["orleans.messaging.ownership-id"], jobs[1].Metadata!["orleans.messaging.ownership-id"]);
    }

    private async Task<Attempt> PrepareAttemptAsync(string routeSuffix)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var route = "messages/" + routeSuffix;
        var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(109, routeSuffix) with { ForwardTo = GrainId.Create("output", routeSuffix) }, route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var preparation = outbox.BlockNextPreparation();
        handler.Release();
        await preparation.WaitAsync();
        return new(receiver, context, outbox, handler, preparation, envelope.Value);
    }

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private static Task OnTurnTaskAsync(IGrainContext context, Func<Task> action)
    {
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { started.SetResult(action()); }
            catch (Exception exception) { started.SetException(exception); }
        });
        return started.Task.Unwrap();
    }

    private sealed record Attempt(IDurableMessagingTestGrain Receiver, IGrainContext Context, JournaledTestOutbox Outbox,
        HandlerProbe.Barrier Handler, JournaledTestOutbox.PreparationBarrier Preparation, DurableEnvelope Envelope) : IDisposable
    {
        public DurableMessagingTestGrain Grain { get; } = Assert.IsType<DurableMessagingTestGrain>(Context.GrainInstance);
        public IJournaledStateManager Manager => Context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        public JournalId JournalId => JournalId.FromGrainId(Receiver.GetGrainId());
        public void Dispose() { Preparation.Dispose(); Handler.Dispose(); }
    }
}
