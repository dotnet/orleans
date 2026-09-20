using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxHandlerSendBoundaryTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreparationSend_IsRejectedBeforeSharedMutationEvenWhenCaught(bool throughOutbox, bool returnAction)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var effects = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEffect>>("test-effects");
        using var handler = new SendingHandler(throughOutbox, sendDuringPreparation: true, returnAction, effects);
        await OnTurnAsync(context, () => context.ActivationServices.GetRequiredService<IDurableInbox>().RegisterHandler("guarded/send", handler));
        using var envelope = CreateEnvelope(receiver, NewMessage(301, "prepare-send"), "guarded/send");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        handler.BeginSend.TrySetResult();
        await handler.SendAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var rejection = Assert.IsType<InvalidOperationException>(handler.Rejection);
        Assert.Contains("synchronous apply action", rejection.Message, StringComparison.Ordinal);
        await OnTurnAsync(context, () =>
        {
            Assert.Empty(outbox);
            Assert.Equal(0, outbox.SendCalls);
            Assert.Equal(envelope.Value.MessageId, handler.Context!.Envelope.MessageId);
            Assert.Equal(receiver.GetGrainId(), handler.Context.GrainId);
            Assert.Equal(receiver.GetGrainId(), handler.Output.SenderId);
            Assert.False(handler.Context.Outbox.TryGetMessage(handler.Output.MessageId, out _));
            Assert.Empty(handler.Context.Outbox.Messages);
            Assert.Equal(0, handler.Context.Outbox.Count);
            context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "interleaved-safe-write";
        });
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(outbox.LastCapturedIds);
        Assert.Equal(0, handler.Applied);
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        handler.FinishPreparation.TrySetResult();
        Assert.Same(rejection, await grain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Same(rejection, outbox.Failure);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        Assert.Empty(outbox);
        Assert.Equal(0, outbox.SendCalls);
        Assert.Equal(0, handler.Applied);
        Assert.Empty(grain.GetSnapshotForTest().InboxDeadLetters);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(outbox.PreparedBatches).DisposeCalls);
        Assert.Equal(0, outbox.JournalPreparationCalls);
        await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Equal(envelope.Value.MessageId, Assert.Single(recovered.InboxDeadLetters).MessageId);
        Assert.Empty(Fixture.GetStagedOutput(receiver));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount("orleans.messaging.outbox-drain", receiver.GetGrainId()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySend_PersistsPreparedOutputAndRejectsRetainedContextAfterAttempt(bool throughOutbox)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var effects = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEffect>>("test-effects");
        using var handler = new SendingHandler(throughOutbox, sendDuringPreparation: false, returnAction: true, effects);
        await OnTurnAsync(context, () => context.ActivationServices.GetRequiredService<IDurableInbox>().RegisterHandler("guarded/send", handler));
        using var envelope = CreateEnvelope(receiver, NewMessage(302, "apply-send"), "guarded/send");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(outbox);
        Assert.Empty(effects);
        Assert.Equal(0, handler.Applied);
        handler.BeginSend.TrySetResult();
        handler.FinishPreparation.TrySetResult();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, handler.Applied);
        Assert.Equal(1, outbox.SendCalls);
        Assert.Equal(handler.Output.MessageId, Assert.Single(outbox).Key);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        await OnTurnAsync(context, () =>
        {
            Assert.True(handler.Context!.Outbox.TryGetMessage(handler.Output.MessageId, out var stored));
            Assert.Equal(handler.Output.MessageId, stored.MessageId);
            Assert.Equal(handler.Output.MessageId, Assert.Single(handler.Context.Outbox.Messages).MessageId);
            Assert.Throws<InvalidOperationException>(() => handler.Send(handler.Batch));
            Assert.Equal(1, outbox.SendCalls);
            Assert.Single(outbox);
        });
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.False(grain.Faulted.Task.IsCompleted);
        await receiver.RequestDeactivationAsync();
        var replayed = await receiver.GetSnapshotAsync();
        Assert.Equal(1, replayed.OutboxCount);
        Assert.Equal(handler.Output.MessageId, Assert.Single(Fixture.GetStagedOutput(receiver)).MessageId);
        Assert.Equal(1, replayed.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(replayed.Effects).Count);
        Assert.Equal(1, Assert.Single(outbox.PreparedBatches).DisposeCalls);
        Assert.Equal(0, outbox.JournalPreparationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionSend_IsRejectedWithoutAccessingSharedOutbox(bool throughOutbox)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        // Application-owned raw capability: selection must reject its phase before ownership.
        using var output = CreateEnvelope(receiver, NewMessage(307, "selection-output"), "output");
        var batch = await OnTurnAsync(context, async () =>
            await outbox.PrepareSendAsync([output.Value], TestContext.Current.CancellationToken));
        try
        {
            var handler = new SelectionHandler(throughOutbox, batch);
            await OnTurnAsync(context, () => context.ActivationServices.GetRequiredService<IDurableInbox>().RegisterHandler(handler));
            using var envelope = CreateEnvelope(receiver, NewMessage(303, "selection"), "guarded/selection");
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverAsync(receiver, envelope.Value));
            Assert.Contains("selection is read-only", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<InvalidOperationException>(handler.Rejection);
            Assert.Equal(0, handler.Prepared);
            Assert.Equal(1, outbox.PreparationsStarted);
            Assert.Equal(1, outbox.PreparationsCompleted);
            var observation = Assert.Single(outbox.PreparedBatches);
            Assert.Equal(output.Value.MessageId, Assert.Single(observation.MessageIds));
            Assert.Equal(0, observation.DisposeCalls);
            Assert.Equal(0, outbox.SendCalls);
            Assert.Empty(outbox);
            Assert.Empty(Fixture.GetStagedOutput(receiver));
        }
        finally { batch.Dispose(); }
        Assert.Equal(1, Assert.Single(outbox.PreparedBatches).DisposeCalls);
        Assert.Equal(0, outbox.JournalPreparationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyFailure_ClosesSendScopeAndReplaysWithoutUnacknowledgedOutput(bool throughOutbox)
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var effects = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEffect>>("test-effects");
        using var handler = new SendingHandler(throughOutbox, false, true, effects);
        var failure = new IOException("Action failed after a legitimate send.");
        handler.ApplyFailure = failure;
        await OnTurnAsync(context, () => context.ActivationServices.GetRequiredService<IDurableInbox>().RegisterHandler("guarded/send", handler));
        using var envelope = CreateEnvelope(receiver, NewMessage(304, "apply-failure"), "guarded/send");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        handler.BeginSend.TrySetResult();
        handler.FinishPreparation.TrySetResult();
        Assert.Same(failure, await grain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Applied);
        Assert.Equal(1, outbox.SendCalls);
        Assert.Single(outbox);
        Assert.Throws<InvalidOperationException>(() => handler.Send(handler.Batch));
        Assert.Equal(1, outbox.SendCalls);
        Assert.Same(failure, outbox.Failure);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(outbox.PreparedBatches).DisposeCalls);
        Assert.Equal(0, outbox.JournalPreparationCalls);
        await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Empty(Fixture.GetStagedOutput(receiver));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RetainedContext_CannotSendFromAnotherAttemptEvenWhenCaught(bool throughOutbox, bool caught)
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var effects = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEffect>>("test-effects");
        using var first = new SendingHandler(throughOutbox, false, true, effects);
        using var second = new SendingHandler(throughOutbox, false, true, effects);
        await OnTurnAsync(context, () =>
        {
            var inbox = context.ActivationServices.GetRequiredService<IDurableInbox>();
            inbox.RegisterHandler("guarded/first", first);
            inbox.RegisterHandler("guarded/second", second);
        });
        using var one = CreateEnvelope(receiver, NewMessage(305, "first"), "guarded/first");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, one.Value)).Status);
        await first.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        first.BeginSend.TrySetResult();
        first.FinishPreparation.TrySetResult();
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        using var two = CreateEnvelope(receiver, NewMessage(306, "second"), "guarded/second");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, two.Value)).Status);
        await second.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(2, outbox.PreparedBatches.Count);
        Assert.Equal(1, outbox.PreparedBatches[0].DisposeCalls);
        Assert.Equal(0, outbox.PreparedBatches[1].DisposeCalls);
        Exception? rejection = null;
        await OnTurnAsync(context, () => second.Applying = () =>
        {
            try { first.Send(second.Batch); }
            catch (InvalidOperationException exception)
            {
                rejection = exception;
                if (!caught) throw;
            }
        });
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        second.BeginSend.TrySetResult();
        second.FinishPreparation.TrySetResult();
        var failure = await grain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Same(Assert.IsType<InvalidOperationException>(rejection), failure);
        Assert.Equal(1, first.Applied);
        Assert.Equal(1, second.Applied);
        Assert.Equal(1, outbox.SendCalls);
        Assert.Equal(first.Output.MessageId, Assert.Single(outbox).Key);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(2, outbox.PreparedBatches.Count);
        Assert.All(outbox.PreparedBatches, batch => Assert.Equal(1, batch.DisposeCalls));
        Assert.Equal(0, outbox.JournalPreparationCalls);
        await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.Equal(one.Value.MessageId, Assert.Single(recovered.Effects).LogicalId);
        Assert.Equal(two.Value.MessageId, Assert.Single(recovered.InboxDeadLetters).MessageId);
        Assert.Equal(first.Output.MessageId, Assert.Single(Fixture.GetStagedOutput(receiver)).MessageId);
    }

    private sealed class SendingHandler(bool throughOutbox, bool sendDuringPreparation, bool returnAction,
        IDurableDictionary<Guid, DurableEffect> effects) : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BeginSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishPreparation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IInboxHandlerContext? Context { get; private set; }
        public DurableEnvelope Output { get; private set; }
        public IPreparedOutboxBatch Batch { get; private set; } = null!;
        public Exception? Rejection { get; private set; }
        public int Applied { get; private set; }
        public Exception? ApplyFailure { get; set; }
        public Action? Applying { get; set; }
        private IDurableOutbox? _outbox;
        public bool CanHandle(IInboxHandlerContext context) => context.Envelope.RouteKey == "guarded/send";
        public async ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            Context = context;
            _outbox = context.Outbox;
            Output = context.CreateEnvelope().To(context.GrainId, "output").WithBody(42).Build();
            Batch = await context.Outbox.PrepareSendAsync([Output], cancellationToken);
            Prepared.TrySetResult();
            await BeginSend.Task.WaitAsync(cancellationToken);
            if (sendDuringPreparation)
            {
                try { Send(Batch); }
                catch (InvalidOperationException exception) { Rejection = exception; }
            }
            SendAttempted.TrySetResult();
            await FinishPreparation.Task.WaitAsync(cancellationToken);
            if (!returnAction) throw new IOException("Preparation failed after attempted send.");
            return () =>
            {
                Applied++;
                effects[context.Envelope.MessageId] = new DurableEffect(context.Envelope.MessageId, 1, 302, "apply-send");
                Applying?.Invoke();
                Send(Batch);
                if (ApplyFailure is { } failure) throw failure;
            };
        }
        public void Send(IPreparedOutboxBatch batch)
        {
            if (throughOutbox) _outbox!.Send(batch);
            else Context!.Send(batch);
        }
        public void Dispose() { BeginSend.TrySetResult(); FinishPreparation.TrySetResult(); }
    }

    private sealed class SelectionHandler(bool throughOutbox, IPreparedOutboxBatch batch) : IInboxHandler
    {
        public Exception? Rejection { get; private set; }
        public int Prepared { get; private set; }
        public bool CanHandle(IInboxHandlerContext context)
        {
            try
            {
                if (throughOutbox) context.Outbox.Send(batch);
                else context.Send(batch);
            }
            catch (InvalidOperationException exception) { Rejection = exception; throw; }
            return true;
        }
        public ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            Prepared++;
            return ValueTask.FromResult<Action>(() => { });
        }
    }

    private static Task<T> OnTurnAsync<T>(IGrainContext context, Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() => { _ = CompleteAsync(); });
        return completion.Task;

        async Task CompleteAsync()
        {
            try { completion.SetResult(await action()); }
            catch (Exception exception) { completion.SetException(exception); }
        }
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
}
