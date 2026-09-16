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
}
