using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MessagingRoutingAndDeadLetterTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task InboxDeadLettersRetainNewestEntriesWithinConfiguredCapacity()
    {
        var receiver = NewGrain();
        var messageIds = new List<MessageId>();
        for (var sequence = 0; sequence < 3; sequence++)
        {
            var envelope = CreateEnvelope(
                receiver,
                new TestMessage(TestApplicationProtocol.NewMessageId(),
                    20 + sequence,
                    $"dead-letter-{sequence}",
                    ThrowDuringPreparation: true));
            messageIds.Add(envelope.Envelope.MessageId);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
            await Fixture.SnapshotProbe.WaitAsync(
                receiver.GetGrainId(),
                snapshot => snapshot.InboxDeadLetters.Any(entry => entry.MessageId == envelope.Envelope.MessageId));
        }

        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(2, state.InboxDeadLetters.Count);
        Assert.DoesNotContain(state.InboxDeadLetters, entry => entry.MessageId == messageIds[0]);
        Assert.Contains(state.InboxDeadLetters, entry => entry.MessageId == messageIds[1]);
        Assert.Contains(state.InboxDeadLetters, entry => entry.MessageId == messageIds[2]);
    }

    [Fact]
    public async Task ActivationRemovesExpiredInboxDeadLetters()
    {
        var receiver = NewGrain();
        var envelope = CreateEnvelope(
            receiver,
            new TestMessage(TestApplicationProtocol.NewMessageId(),
                30,
                "expired-dead-letter",
                ThrowDuringPreparation: true));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        var before = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Fixture.Clock.Advance(TimeSpan.FromHours(2));
        await ReactivateAsync(receiver);
        var after = await receiver.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, after.ActivationId);
        Assert.Empty(after.InboxDeadLetters);

        await ReactivateAsync(receiver);
        Assert.Empty((await receiver.GetSnapshotAsync()).InboxDeadLetters);
    }

    [Fact]
    public async Task MalformedTypedBody_DeadLettersAndDoesNotBlockLaterValidMessage()
    {
        var receiver = NewGrain();
        var malformed = CreateEnvelope(receiver, "wrong-body", "typed");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, malformed)).Status);
        var poisoned = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.Empty(poisoned.Effects);
        var deadLetter = Assert.Single(poisoned.InboxDeadLetters);
        Assert.Equal(malformed.Envelope.MessageId, deadLetter.MessageId);
        Assert.Contains(nameof(TestMessage), deadLetter.Reason, StringComparison.Ordinal);

        var valid = CreateEnvelope(receiver, NewMessage(41, "valid-after-poison"), "typed");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, valid)).Status);
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("valid-after-poison", Assert.Single(recovered.Effects).Value);
        Assert.Single(recovered.InboxDeadLetters);
    }

    [Fact]
    public async Task InboxDeadLetterRemoval_IsDurable()
    {
        var receiver = NewGrain();
        var malformed = CreateEnvelope(receiver, "wrong-body", "typed");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, malformed)).Status);
        _ = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.True(await receiver.RemoveInboxDeadLetterAsync(
            malformed.Envelope.MessageId));
        Assert.Empty((await receiver.GetSnapshotAsync()).InboxDeadLetters);
        Assert.False(await receiver.RemoveInboxDeadLetterAsync(
            malformed.Envelope.MessageId));

        await receiver.RequestDeactivationAsync();
        Assert.Empty((await receiver.GetSnapshotAsync()).InboxDeadLetters);
    }

    [Fact]
    public async Task HandlerNotFound_IsRejectedWithoutInboxPersistence()
    {
        var receiver = NewGrain();
        await receiver.ConfigureHandlerAsync(false);
        await ReactivateAsync(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(71, "missing"), "unknown/route");

        var result = await DeliverAsync(receiver, envelope);

        Assert.Equal(DeliveryStatus.HandlerNotFound, result.Status);
        Assert.Equal("No inbox handler is registered.", result.Message);
        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(0, state.InboxCount);
        Assert.Empty(state.Effects);
    }

    [Fact]
    public async Task Deliver_ImplicitReceiverNamespacesTheSameIncomingCommand()
    {
        var receiver = NewGrain();
        var declaredReceiver = NewGrain();
        var envelope = CreateEnvelope(
            declaredReceiver,
            NewMessage(74, "wrong-receiver"));

        Assert.Null(typeof(InboxMessage).GetProperty("ReceiverId"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        var first = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(envelope.Envelope.MessageId, Assert.Single(first.Effects).LogicalId);
        Assert.Equal(1, Assert.Single(first.Effects).Count);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(declaredReceiver, envelope)).Status);
        var delivered = await Fixture.WaitForEffectCountAsync(declaredReceiver, 1);
        Assert.Equal(envelope.Envelope.MessageId, Assert.Single(delivered.Effects).LogicalId);
        Assert.Equal(1, Assert.Single(delivered.Effects).Count);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(declaredReceiver, envelope)).Status);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).ProcessedMessageCount);
        Assert.Equal(1, (await declaredReceiver.GetSnapshotAsync()).ProcessedMessageCount);
    }

}
