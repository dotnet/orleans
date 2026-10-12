using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Session;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxAdmissionTests : MessagingBehaviorTestBase
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
    }

    [Fact]
    public async Task HandlerStorageFailure_DeactivatesAndFailsQueuedWritesAfterPriorAcknowledgement()
    {
        using var attempt = await PrepareAttemptAsync("fault-ordering");
        IInboxExtension extension = attempt.Context.ActivationServices.GetRequiredService<Inbox>();
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
        // A failed pump stops the retired activation's inbox; later callers are rerouted to a fresh activation.
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await extension.DeliverAsync(attempt.Envelope, TestContext.Current.CancellationToken));
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
    public async Task LateOutgoingIntent_AfterCapture_WaitsForNextCapture()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IOutbox>();
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var durable = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, OutboxMessage>>("test-handler-output");
        var sessions = Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var first = TestApplicationProtocol.Create(sessions, receiver.GetGrainId(), receiver.GetGrainId(), "messages/output", 1);
        var late = TestApplicationProtocol.Create(sessions, receiver.GetGrainId(), receiver.GetGrainId(), "messages/output", 2);
        await receiver.StageOutputAsync(first);
        var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var write = Fixture.WriteStateAsync(receiver).AsTask();
        await storage.WaitUntilEnteredAsync();
        await OnTurnTaskAsync(context, async () =>
        {
            outbox.Send(late);
        });
        Assert.Equal(new[] { first.Envelope.MessageId }, grain.OutputCaptures[^1]);
        Assert.Equal(2, outbox.Count);
        storage.Release();
        await write;
        Assert.Equal(new[] { first.Envelope.MessageId }, grain.OutputCaptures[^1]);
        await receiver.RetryWriteStateAsync();
        Assert.Equal(new[] { first.Envelope.MessageId, late.Envelope.MessageId }.OrderBy(static key => key.ToString(), StringComparer.Ordinal),
            grain.OutputCaptures[^1].OrderBy(static key => key.ToString(), StringComparer.Ordinal));
        Assert.Equal(2, durable.Count);
        await receiver.RequestDeactivationAsync();
        Assert.Equal(2, (await receiver.GetSnapshotAsync()).OutboxCount);
    }

    [Fact]
    public async Task AcceptanceAfterCapture_IsAcknowledgedByLaterOperation()
    {
        var receiver = NewGrain();
        var seed = CreateEnvelope(receiver, NewMessage(110, "seed"), "messages/cutoff");
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);

        // The pump runs alongside grain calls, so holding the seed's handler keeps it pending in the inbox.
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/cutoff");
        IInboxExtension extension = context.ActivationServices.GetRequiredService<Inbox>();
        Task<DeliveryResult> initial = null!;
        await OnTurnAsync(context, () => initial = extension.DeliverAsync(seed, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(DeliveryStatus.Accepted, (await initial).Status);
        await handler.WaitUntilEnteredAsync();
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        grain.ClearCaptures();
        await OnTurnAsync(context, () => context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "capture-cutoff");
        var preceding = Fixture.WriteStateAsync(receiver).AsTask();
        await storage.WaitUntilEnteredAsync();
        var incoming = CreateEnvelope(receiver, NewMessage(111, "late-acceptance"), "messages/cutoff");
        Task<DeliveryResult> delivery = null!;
        await OnTurnAsync(context, () => delivery = extension.DeliverAsync(incoming).AsTask());
        Assert.False(delivery.IsCompleted);
        Assert.Equal(2, grain.GetSnapshotForTest().InboxCount);
        storage.Release();
        await preceding;
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        Assert.Equal(1, grain.Captures[0].InboxCount);
        Assert.Equal(2, grain.Captures[1].InboxCount);
        Assert.Equal(2, grain.GetSnapshotForTest().InboxCount);
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
        var envelope = CreateEnvelope(receiver, NewMessage(112, "cancel-wait"));
        using var cancellation = new CancellationTokenSource();
        var delivery = DeliverWithCancellationAsync(receiver, envelope, cancellation.Token);
        await storage.WaitUntilEnteredAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        storage.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
    }

    [Fact]
    public async Task MessagingPrimaryStates_AreTheRegisteredScopedDataInstances()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(receiver).ActivationServices;
        var manager = services.GetRequiredService<IJournaledStateManager>();
        Assert.True(manager.TryGetStateMachine(MessagingStateNames.Inbox, out var inbox));
        Assert.Same(services.GetRequiredService<Inbox>(), inbox);
        Assert.True(manager.TryGetStateMachine("test-handler-output", out var output));
        Assert.Same(((JournaledTestOutbox)services.GetRequiredService<IOutbox>()).StoredMessages, output);
        Assert.Same(services.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, OutboxMessage>>("test-handler-output"), output);
    }

    [Fact]
    public async Task AcceptanceWriteFailure_DeactivatesAndFreshScopeRetries()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Fixture.Storage.FailWrite(journal);
        var envelope = CreateEnvelope(receiver, NewMessage(114, "acceptance-write-failure"));
        var failure = await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope));
        Assert.Contains("Injected journal write failure", failure.Message, StringComparison.Ordinal);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var rejected = grain.GetSnapshotForTest();
        Assert.Equal(before.ActivationId, rejected.ActivationId);
        Assert.Equal(1, rejected.InboxCount);
        Assert.Empty(rejected.Effects);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        var deactivation = await grain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.IsType<IOException>(deactivation);
        Assert.Equal(failure.Message, deactivation.Message);
        Assert.NotEmpty(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, receiver.GetGrainId()));
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(rejected.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
    }

    private async Task<Attempt> PrepareAttemptAsync(string routeSuffix)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var route = "messages/" + routeSuffix;
        var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var envelope = CreateEnvelope(receiver, NewMessage(109, routeSuffix) with { ForwardTo = GrainId.Create("output", routeSuffix) }, route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IOutbox>();
        var preparation = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route + "/application-preparation");
        handler.Release();
        await preparation.WaitUntilEnteredAsync();
        return new(receiver, context, outbox, handler, preparation, envelope);
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

    private sealed record Attempt(IMessagingTestGrain Receiver, IGrainContext Context, JournaledTestOutbox Outbox,
        HandlerProbe.Barrier Handler, HandlerProbe.Barrier Preparation, InboxMessage Envelope) : IDisposable
    {
        public MessagingTestGrain Grain { get; } = Assert.IsType<MessagingTestGrain>(Context.GrainInstance);
        public IJournaledStateManager Manager => Context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        public JournalId JournalId => JournalId.FromGrainId(Receiver.GetGrainId());
        public void Dispose() { Preparation.Dispose(); Handler.Dispose(); }
    }

    private sealed class CallbackContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }
}
