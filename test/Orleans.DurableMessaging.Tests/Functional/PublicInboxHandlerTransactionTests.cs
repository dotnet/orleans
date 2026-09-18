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
public sealed class PublicInboxHandlerTransactionTests : DurableMessagingBehaviorTestBase
{
    public PublicInboxHandlerTransactionTests() : base(receiverOnly: false)
    {
    }

    [Fact]
    public async Task HandlerSuccess_CommitsEffectCompletionDedupeAndOutgoingAtomically()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        var logicalId = Guid.NewGuid();
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(logicalId, 7, "atomic", sink.GetGrainId()));

        var result = await DeliverAsync(receiver, envelope.Value);
        var receiverState = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var sinkState = await Fixture.WaitForEffectCountAsync(sink, 1);
        receiverState = await Fixture.WaitForOutboxCountAsync(receiver, 0);

        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        var effect = Assert.Single(receiverState.Effects);
        Assert.Equal(new DurableEffect(logicalId, 1, 7, "atomic"), effect);
        Assert.Equal(0, receiverState.InboxCount);
        Assert.Equal(0, receiverState.OutboxCount);
        Assert.Equal(effect, Assert.Single(sinkState.Effects));

        var duplicate = await DeliverAsync(receiver, envelope.Value);
        Assert.Equal(DeliveryStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
        Assert.Equal(1, Assert.Single((await sink.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task HandlerPreparationFailure_DeadLettersWithoutEffectsOrSinkDelivery()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(Guid.NewGuid(), 9, "preparation-failure", sink.GetGrainId(), ThrowDuringPreparation: true));

        var accepted = await DeliverAsync(receiver, envelope.Value);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Equal(DeliveryStatus.Accepted, accepted.Status);
        Assert.Empty(state.Effects);
        Assert.Equal(0, state.InboxCount);
        Assert.Equal(0, state.OutboxCount);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Equal(envelope.Value.MessageId, deadLetter.MessageId);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.Contains("Injected handler preparation failure", deadLetter.Reason, StringComparison.Ordinal);
        Assert.Empty((await sink.GetSnapshotAsync()).Effects);
    }
    [Fact]
    public async Task HandlerAndRealOutboxPreparation_SerializeCaptureAndPreserveCancelledWriteWait()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/admitted-outgoing");
        using var envelope = CreateEnvelope(receiver,
            new DurableTestMessage(Guid.NewGuid(), 91, "admitted", sink.GetGrainId()), "messages/admitted-outgoing");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var localPreparation = grain.GetSnapshotForTest();
        Assert.Empty(localPreparation.Effects);
        Assert.Equal(1, localPreparation.InboxCount);
        Assert.Equal(0, localPreparation.ProcessedMessageCount);
        Assert.Equal(0, localPreparation.OutboxCount);
        using var scheduling = Fixture.JobManagerProbe.BlockNext("orleans.messaging.outbox-flush");
        handler.Release();
        await scheduling.WaitUntilEnteredAsync();
        var preparing = grain.GetSnapshotForTest();
        Assert.Single(preparing.Effects);
        Assert.Equal(0, preparing.InboxCount);
        Assert.Equal(1, preparing.ProcessedMessageCount);
        Assert.Equal(1, preparing.OutboxCount);
        Assert.Null(preparing.OutboxJob);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        using var cancellation = new CancellationTokenSource();
        var canceledWait = manager.WriteStateAsync(cancellation.Token).AsTask();
        var completedWait = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(canceledWait.IsCompleted);
        Assert.False(completedWait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        try
        {
            scheduling.Continue();
            await storage.WaitUntilEnteredAsync();
            var captured = grain.GetSnapshotForTest();
            Assert.Equal(0, captured.InboxCount);
            Assert.Equal(1, captured.ProcessedMessageCount);
            Assert.Equal(1, captured.OutboxCount);
            Assert.Single(captured.Effects);
            var owner = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs("orleans.messaging.outbox-flush", receiver.GetGrainId()));
            Assert.Equal(owner.Id, captured.OutboxJob?.Id);
            Assert.Equal(owner.ShardId, captured.OutboxJob?.ShardId);
            Assert.Equal(owner.Metadata!["orleans.messaging.ownership-id"], captured.OutboxJobId);
            Assert.False(completedWait.IsCompleted);
            Assert.Empty((await sink.GetSnapshotAsync()).Effects);
        }
        finally
        {
            storage.Release();
        }
        await completedWait;
        var delivered = await Fixture.WaitForEffectCountAsync(sink, 1);
        var completed = await Fixture.WaitForOutboxCountAsync(receiver, 0);
        Assert.Equal(Assert.Single(completed.Effects), Assert.Single(delivered.Effects));
        Assert.DoesNotContain(grain.Captures, snapshot => snapshot.Effects.Count > 0 && snapshot.ProcessedMessageCount == 0);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerCompletionPersistenceFailure_FreshActivationConvergesEffectDedupeAndSink(bool storageCommitted)
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldGrain = Assert.IsType<DurableMessagingTestGrain>(oldContext.GrainInstance);
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/completion-outcome");
        var message = new DurableTestMessage(Guid.NewGuid(), 92, "completion-outcome", sink.GetGrainId());
        using var envelope = CreateEnvelope(receiver, message, "messages/completion-outcome");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        if (storageCommitted)
        {
            Fixture.Storage.FailAfterWrite(journal);
        }
        else
        {
            Fixture.Storage.FailWrite(journal);
        }
        handler.Release();
        Assert.IsType<IOException>(await oldGrain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var delivered = await Fixture.WaitForEffectCountAsync(sink, 1);
        completed = await Fixture.WaitForOutboxCountAsync(receiver, 0);

        Assert.NotEqual(before.ActivationId, completed.ActivationId);
        Assert.Equal(new DurableEffect(message.LogicalId, 1, message.Sequence, message.Value), Assert.Single(completed.Effects));
        Assert.Equal(Assert.Single(completed.Effects), Assert.Single(delivered.Effects));
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(0, completed.InboxCount);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Empty(completed.OutboxDeadLetters);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(1, Assert.Single((await sink.GetSnapshotAsync()).Effects).Count);
    }

}
