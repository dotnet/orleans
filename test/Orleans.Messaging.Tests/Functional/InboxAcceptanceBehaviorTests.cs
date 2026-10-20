using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxAcceptanceBehaviorTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task Deliver_AcceptedOnlyAfterInboxIsDurableAndWakeupIsScheduled()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var barrier = Fixture.Storage.BlockWrite(journalId);
        var envelope = CreateEnvelope(receiver, NewMessage(1, "durability"));

        var delivery = DeliverAsync(receiver, envelope);
        await barrier.WaitUntilEnteredAsync();

        Assert.False(delivery.IsCompleted);
        var staged = Fixture.GetSnapshot(receiver);
        Assert.Equal(1, staged.InboxCount);
        Assert.Empty(staged.Effects);
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, receiver.GetGrainId()));

        barrier.Release();
        var result = await delivery;
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        EndpointSnapshot completed;
        try
        {
            completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Accepted message did not drain. Inbox={snapshot.InboxCount}, effects={snapshot.Effects.Count}, deadLetters={snapshot.InboxDeadLetters.Count}.",
                exception);
        }
        Assert.Single(completed.Effects);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(2, Fixture.Storage.GetSuccessfulWriteCount(journalId));
    }

    [Fact]
    public async Task ConcurrentDuplicateDeliveries_ConvergeToOneEffectWithinRetention()
    {
        var receiver = NewGrain();
        using var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/blocked-duplicate");
        var envelope = CreateEnvelope(receiver, NewMessage(11, "duplicate"), "messages/blocked-duplicate");

        var first = await DeliverAsync(receiver, envelope);
        await barrier.WaitUntilEnteredAsync();
        var second = DeliverAsync(receiver, envelope);

        // The pending acceptance is durable, so the duplicate is answered without waiting for the handler.
        Assert.Equal(DeliveryStatus.Accepted, first.Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await second).Status);
        Assert.Equal(1, Fixture.GetSnapshot(receiver).InboxCount);

        barrier.Release();
        var state = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(state.Effects).Count);
        Assert.Equal(1, state.MaxConcurrentHandlers);
    }

    [Fact]
    public async Task Delivery_IsAdmittedWhileANonReentrantRequestIsRunning()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var inbox = Fixture.GetInbox(receiver);

        // The request holds the grain while its journal write waits.
        await receiver.StageEffectAsync(new MessageEffect(MessageId.Create("request", "effect"), 1, 0, "request"));
        using var write = Fixture.Storage.BlockWrite(journal);
        var request = receiver.RetryWriteStateAsync();
        await write.WaitUntilEnteredAsync();

        var envelope = CreateEnvelope(receiver, NewMessage(14, "interleaved"));
        var delivery = DeliverAsync(receiver, envelope);
        await Fixture.WaitUntilAsync(receiver, () => inbox.Count == 1);
        Assert.False(request.IsCompleted);

        write.Release();
        await request;
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        Assert.Equal("interleaved", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 2)).Effects, static effect => effect.Value == "interleaved").Value);
    }
    [Fact]
    public async Task DuplicateOfCompletedMessage_WaitsUntilCompletionIsDurable()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var envelope = CreateEnvelope(receiver, NewMessage(12, "completion-durability"));

        // The first write accepts the message and the second records its completion.
        var barrier = Fixture.Storage.BlockWrite(journalId, matchingWrite: 2);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await barrier.WaitUntilEnteredAsync();

        // Whenever the duplicate is answered, the completion it depends on must already be durable.
        var duplicate = DeliverAsync(receiver, envelope).ContinueWith(
            task => (task.Result.Status, Writes: Fixture.Storage.GetSuccessfulWriteCount(journalId)),
            TaskScheduler.Default);

        barrier.Release();
        var (status, writes) = await duplicate;
        Assert.Equal(DeliveryStatus.Duplicate, status);
        Assert.True(writes >= 2, $"The duplicate was answered after {writes} durable writes, before its completion was durable.");
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task DuplicateAfterReactivationWithinRetention_RemainsEffectivelyOnce()
    {
        var receiver = NewGrain();
        var envelope = CreateEnvelope(receiver, NewMessage(13, "reactivation"));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        var before = await Fixture.WaitForEffectCountAsync(receiver, 1);
        await receiver.RequestDeactivationAsync();
        var after = await receiver.GetSnapshotAsync();

        Assert.NotEqual(before.ActivationId, after.ActivationId);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task KnownSentAt_OlderAndEqualResendsDoNotWriteOrShortenRetention()
    {
        var receiver = NewGrain();
        var message = CreateEnvelope(receiver, NewMessage(31, "same-command"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await Fixture.WaitForInboxCountAsync(receiver, 0);
        await receiver.RetryWriteStateAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message with { SentAt = message.SentAt.AddTicks(-1) })).Status);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        var inbox = Fixture.GetInbox(receiver);
        Assert.Equal(message.SentAt, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedSentAt(inbox, message.Envelope.MessageId)));
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerSentAt_PendingAndProcessedResendsWaitForRetentionDurability(bool completed)
    {
        var receiver = NewGrain();
        const string route = "messages/sent-time-retention";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var message = CreateEnvelope(receiver, NewMessage(32, "retained-command"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await handler.WaitUntilEnteredAsync();
        if (completed)
        {
            handler.Release();
            await Fixture.WaitForInboxCountAsync(receiver, 0);
        }

        await receiver.RetryWriteStateAsync();
        var beforeResends = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message with { SentAt = message.SentAt.AddTicks(-1) })).Status);
        Assert.Equal(beforeResends, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        Fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var newer = message with { SentAt = Fixture.JobNow };
        var inbox = Fixture.GetInbox(receiver);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        using var acknowledgement = Fixture.Storage.BlockAcknowledgement(journal);
        var updating = DeliverAsync(receiver, newer);
        await acknowledgement.WaitUntilEnteredAsync();
        Assert.False(updating.IsCompleted);
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(newer.SentAt, await Fixture.OnGrainAsync(receiver, () => completed
            ? InboxTestAccess.GetProcessedSentAt(inbox, message.Envelope.MessageId)
            : InboxTestAccess.GetPendingMessages(inbox)[message.Envelope.MessageId].Message.SentAt));
        var olderReply = await Fixture.OnGrainAsync(receiver, () => inbox.DeliverAsync(message, TestContext.Current.CancellationToken).AsTask());
        Assert.False(olderReply.IsCompleted);
        acknowledgement.Release();
        Assert.Equal(DeliveryStatus.Duplicate, (await updating).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await olderReply).Status);
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));

        handler.Release();
        await Fixture.WaitForInboxCountAsync(receiver, 0);
        var state = await receiver.GetSnapshotAsync();
        Assert.Equal("retained-command", Assert.Single(state.Effects).Value);
        Assert.Equal(1, Assert.Single(state.Effects).Count);
        Assert.Equal(1, state.ProcessedMessageCount);
        Assert.Equal(newer.SentAt, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedSentAt(inbox, message.Envelope.MessageId)));
    }

    [Fact]
    public async Task SameMessageIdFromDistinctSenders_DeduplicatesOneReceiverCommand()
    {
        var receiver = NewGrain();
        var original = CreateEnvelope(receiver, NewMessage(14, "sender-scoped"));
        var other = TestApplicationProtocol.With(original, Sessions,
            sender: GrainId.Create("other-sender", "same-message-id"));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, original)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, other)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);

        var previous = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, original)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, other)).Status);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(completed.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
    }

    [Fact]
    public async Task ReorderedDistinctAndDuplicateMessages_ConvergeByApplicationSequence()
    {
        var receiver = NewGrain();
        var messages = new[]
        {
            NewMessage(3, "third"),
            NewMessage(1, "first"),
            NewMessage(2, "second"),
        };

        foreach (var message in messages)
        {
            var envelope = CreateEnvelope(receiver, message);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        }

        var state = await Fixture.WaitForEffectCountAsync(receiver, 3);
        Assert.Equal([1, 2, 3], state.Effects.Select(static effect => effect.Sequence));
        Assert.All(state.Effects, static effect => Assert.Equal(1, effect.Count));
    }

    [Fact]
    public async Task ConcurrentDelivery_WaitsWhileHandlersRemainSequential()
    {
        var receiver = NewGrain();
        using var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/sequential");
        var first = CreateEnvelope(receiver, NewMessage(21, "first"), "messages/sequential");
        var second = CreateEnvelope(receiver, NewMessage(22, "second"), "messages/sequential");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first)).Status);
        await WaitForBarrierAsync(receiver, barrier);
        var secondDelivery = DeliverAsync(receiver, second);
        Assert.False(secondDelivery.IsCompleted);

        barrier.Release();
        Assert.Equal(DeliveryStatus.Accepted, (await secondDelivery).Status);
        EndpointSnapshot state;
        try
        {
            state = await Fixture.WaitForEffectCountAsync(receiver, 2);
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Second message did not complete. Inbox={snapshot.InboxCount}, effects={snapshot.Effects.Count}, deadLetters={snapshot.InboxDeadLetters.Count}.",
                exception);
        }
        Assert.Equal(1, state.MaxConcurrentHandlers);
        Assert.Equal([21, 22], state.Effects.Select(static effect => effect.Sequence));
    }
}
