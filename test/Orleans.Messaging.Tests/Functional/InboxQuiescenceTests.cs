using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxQuiescenceTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task OwnerDelete_AfterAcknowledgedWork_StopsPumpAndUsesFreshActivation()
    {
        var receiver = NewGrain();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/delete-ack");
        var first = CreateEnvelope(receiver, NewMessage(160, "first"), "messages/delete-ack");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var pump = Fixture.GetPump(receiver);
        var outbox = GetOutbox(context);
        var second = CreateEnvelope(receiver, NewMessage(161, "second"), "messages/delete-ack");
        var secondDelivery = DeliverAsync(receiver, second);

        handler.Release();
        Assert.Equal(DeliveryStatus.Accepted, (await secondDelivery).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 2);
        Assert.Equal(2, completed.ProcessedMessageCount);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
        using var storage = Fixture.Storage.BlockDelete(JournalId.FromGrainId(receiver.GetGrainId()));
        var deletion = receiver.DeleteStateAndDeactivateAsync();
        await storage.WaitUntilEnteredAsync();
        Assert.True(outbox.Stopping.IsCompleted);
        Assert.True(pump.ShutdownToken.IsCancellationRequested);
        Assert.False(deletion.IsCompleted);
        storage.Release();
        await deletion;
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var deleted = await receiver.GetSnapshotAsync();
        Assert.NotEqual(completed.ActivationId, deleted.ActivationId);
        Assert.Empty(deleted.Effects);
        Assert.Equal(0, deleted.ProcessedMessageCount);
    }

    [Fact]
    public async Task OwnerDelete_DiscardsStagedOutputAndLaterDeliveryUsesFreshActivation()
    {
        var receiver = NewGrain();
        await receiver.StageEffectAsync(new MessageEffect(TestApplicationProtocol.NewMessageId(), 1, 190, "delete-this"));
        await receiver.RetryWriteStateAsync();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var outbox = GetOutbox(context);
        var staged = TestApplicationProtocol.Create(Sessions, receiver.GetGrainId(), receiver.GetGrainId(),
            "output/staged", NewMessage(193, "staged-output"));
        {
            await receiver.StageOutputAsync(staged);
            Assert.Equal(staged.Envelope.MessageId, Assert.Single(outbox.Messages).Envelope.MessageId);
            var journal = JournalId.FromGrainId(receiver.GetGrainId());
            var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
            await receiver.DeleteStateAndDeactivateAsync();
            await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
            Assert.Empty(outbox.Messages);
            Assert.Empty(grain.GetSnapshotForTest().Effects);
            Assert.True(outbox.Stopping.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => outbox.Send(staged));
        }
        Assert.Empty(outbox.Messages);
        var fresh = await receiver.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, fresh.ActivationId);
        Assert.Empty(fresh.Effects);
        Assert.Equal(0, fresh.OutboxCount);
        var envelope = CreateEnvelope(receiver, NewMessage(191, "new-state"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        var after = await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(fresh.ActivationId, after.ActivationId);
        Assert.Equal("new-state", Assert.Single(after.Effects).Value);
        Assert.Equal(1, after.ProcessedMessageCount);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
    }

    private static JournaledTestOutbox GetOutbox(IGrainContext context) =>
        (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IOutbox>();
}
