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
    public async Task UnexpectedApplyFailure_RequestsDeactivationWithoutImplicitWrite()
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
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        var captures = grain.Captures.Count;
        var failure = new IOException("Unexpected Apply failure must not trigger a feature write.");
        await OnTurnAsync(context, () =>
        {
            Assert.Equal(0, manager.PendingWriteByteCount);
            grain.NextApplyFailure = failure;
        });

        handler.Release();
        Assert.Same(failure, await grain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(captures, grain.Captures.Count);
        Assert.Equal(1, Assert.Single(grain.GetSnapshotForTest().Effects).Count);
        Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
        Assert.Equal(0, grain.GetSnapshotForTest().ProcessedMessageCount);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task CanceledOwnerDeleteWait_RetainsStoppedWorkflowThroughResetAndDeactivation()
    {
        var receiver = NewGrain();
        await receiver.StageEffectAsync(new DurableEffect(Guid.NewGuid(), 1, 204, "before-delete"));
        await receiver.RetryWriteStateAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var inbox = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(
            ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var stoppedToken = CancellationCleanupProbe.Field<CancellationTokenSource>(inbox, "_shutdownCts").Token;
        using var storage = Fixture.Storage.BlockDelete(JournalId.FromGrainId(receiver.GetGrainId()));
        using var cancellation = new CancellationTokenSource();
        var deleting = receiver.DeleteStateAndDeactivateAsync();
        var caller = deleting.WaitAsync(cancellation.Token);
        await storage.WaitUntilEnteredAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
        Assert.True(outbox.Stopping.IsCompleted);
        Assert.True(stoppedToken.IsCancellationRequested);
        Assert.False(deleting.IsCompleted);
        Assert.False(context.Deactivated.IsCompleted);
        using var envelope = CreateEnvelope(receiver, NewMessage(205, "after-delete"));
        Task<DeliveryResult> rejected = null!;
        await OnTurnAsync(context, () => rejected = inbox.DeliverAsync(envelope.Value, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await outbox.PrepareSendAsync([envelope.Value], TestContext.Current.CancellationToken));
        Assert.Single(grain.GetSnapshotForTest().Effects);
        storage.Release();
        await deleting;
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        Assert.True(outbox.Stopping.IsCompleted);
        Assert.True(stoppedToken.IsCancellationRequested);
        var fresh = await receiver.GetSnapshotAsync();
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, fresh.ActivationId);
        Assert.Empty(fresh.Effects);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(fresh.ActivationId, completed.ActivationId);
        Assert.Equal("after-delete", Assert.Single(completed.Effects).Value);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
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
