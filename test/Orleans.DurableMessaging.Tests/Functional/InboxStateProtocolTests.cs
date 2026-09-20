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
public sealed class InboxStateProtocolTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task RawWriteDuringLocalPreparation_CapturesNoHandlerEffects()
    {
        var receiver = NewGrain();
        const string route = "messages/local-preparation";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(201, "local"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        await OnTurnAsync(context, () => context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "raw-write");
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.False(grain.ApplyAttempted.Task.IsCompleted);
        var beforeApply = grain.GetSnapshotForTest();
        Assert.Empty(beforeApply.Effects);
        Assert.Equal(1, beforeApply.InboxCount);
        Assert.Equal(0, beforeApply.ProcessedMessageCount);
        var captured = grain.Captures[^1];
        Assert.Empty(captured.Effects);
        Assert.Equal(1, captured.InboxCount);
        Assert.Equal(0, captured.ProcessedMessageCount);
        handler.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        await receiver.RequestDeactivationAsync();
        var replayed = await receiver.GetSnapshotAsync();
        Assert.Equal(completed.Effects, replayed.Effects);
        Assert.Equal(1, replayed.ProcessedMessageCount);
    }

    [Fact]
    public async Task ApplyFailure_FencesQueuedUncapturedWriteBeforePartialCapture()
    {
        var receiver = NewGrain();
        const string route = "messages/apply-failure";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(202, "apply-failure"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        Assert.Equal(0, manager.PendingWriteByteCount);
        await OnTurnAsync(context, () =>
            context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "prior-cohort");
        using var storage = Fixture.Storage.BlockWrite(journal);
        var preceding = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        // The manager lends C's committed buffer to storage until its ACK. Queuing a
        // second write must add no bytes beyond that already-captured borrowed buffer.
        var capturedBytes = manager.PendingWriteByteCount;
        Assert.True(capturedBytes > 0);
        var captures = grain.Captures.Count;
        var earlier = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(capturedBytes, manager.PendingWriteByteCount);
        var acknowledgements = 0;
        outbox.AfterWriteCompleted = () => acknowledgements++;
        var failure = new InvalidOperationException("Injected synchronous apply failure.");
        await OnTurnAsync(context, () => grain.NextApplyFailure = failure);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        handler.Release();
        await grain.ApplyAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await OnTurnAsync(context, () =>
        {
            Assert.Equal(1, Assert.Single(grain.GetSnapshotForTest().Effects).Count);
            Assert.True(manager.PendingWriteByteCount > capturedBytes);
            Assert.True(manager.TryGetStateMachine("__orleans.durable-messaging.inbox", out var inbox));
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(inbox.ValidatePendingChanges));
        });
        Assert.Equal(captures, grain.Captures.Count);
        Assert.Equal(0, acknowledgements);
        storage.Release();
        await preceding;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => earlier));
        Assert.Same(failure, await grain.Faulted.Task);
        Assert.Same(failure, outbox.Failure);
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(1, acknowledgements);
        Assert.Equal(captures, grain.Captures.Count);
        Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
        Assert.Equal(0, grain.GetSnapshotForTest().ProcessedMessageCount);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task ApplyFailure_WithoutEarlierWrite_FencesBeforeAnyPartialCapture()
    {
        var receiver = NewGrain();
        const string route = "messages/no-earlier-write";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(203, "no-earlier-write"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        var captures = grain.Captures.Count;
        var failure = new IOException("Partial Apply must trigger its own execution fence.");
        await OnTurnAsync(context, () =>
        {
            Assert.Equal(0, manager.PendingWriteByteCount);
            grain.NextApplyFailure = failure;
        });

        // No earlier Write is queued: the receiver must admit its terminal flush outside
        // the handler's read-only admission context and fence during execution validation.
        handler.Release();
        Assert.Same(failure, await grain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Same(failure, outbox.Failure);
        var fenced = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.WriteStateAsync(TestContext.Current.CancellationToken));
        Assert.Same(failure, fenced.InnerException);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(captures, grain.Captures.Count);
        Assert.Equal(1, Assert.Single(grain.GetSnapshotForTest().Effects).Count);
        Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
        Assert.Equal(0, grain.GetSnapshotForTest().ProcessedMessageCount);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task CanceledDeleteWait_KeepsAdmissionClosedUntilActualReset()
    {
        var receiver = NewGrain();
        await receiver.StageEffectAsync(new DurableEffect(Guid.NewGuid(), 1, 204, "before-delete"));
        await receiver.RetryWriteStateAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var effects = (ObservedJournalDictionary<Guid, DurableEffect>)context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEffect>>("test-effects");
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        effects.Resetting = () => reset.TrySetResult();
        var storage = Fixture.Storage.BlockDelete(JournalId.FromGrainId(receiver.GetGrainId()));
        using var cancellation = new CancellationTokenSource();
        var deleting = manager.DeleteStateAsync(cancellation.Token).AsTask();
        await storage.WaitUntilEnteredAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deleting);
        using var envelope = CreateEnvelope(receiver, NewMessage(205, "after-delete"));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverAsync(receiver, envelope.Value));
        Assert.Contains("deletion", rejected.Message, StringComparison.Ordinal);
        Assert.False(reset.Task.IsCompleted);
        Assert.Single(grain.GetSnapshotForTest().Effects);
        storage.Release();
        await reset.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(grain.GetSnapshotForTest().ActivationId, completed.ActivationId);
        Assert.Equal("after-delete", Assert.Single(completed.Effects).Value);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.False(grain.Faulted.Task.IsCompleted);
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
