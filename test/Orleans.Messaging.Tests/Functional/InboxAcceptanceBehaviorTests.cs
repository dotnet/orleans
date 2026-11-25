using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging.Configuration;
using Orleans.Durable.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Durable.Messaging.Tests.Functional;

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
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(result);
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
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(first);
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await second);
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
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await delivery);
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
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, envelope));
        await barrier.WaitUntilEnteredAsync();

        // Whenever the duplicate is answered, the completion it depends on must already be durable.
        var duplicate = DeliverAsync(receiver, envelope).ContinueWith(
            task => (task.Result, Writes: Fixture.Storage.GetSuccessfulWriteCount(journalId)),
            TaskScheduler.Default);

        barrier.Release();
        var (result, writes) = await duplicate;
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(result);
        Assert.True(writes >= 2, $"The duplicate was answered after {writes} durable writes, before its completion was durable.");
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task DuplicateAfterReactivationWithinRetention_RemainsEffectivelyOnce()
    {
        var receiver = NewGrain();
        var envelope = CreateEnvelope(receiver, NewMessage(13, "reactivation"));

        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, envelope));
        var before = await Fixture.WaitForEffectCountAsync(receiver, 1);
        await receiver.RequestDeactivationAsync();
        var after = await receiver.GetSnapshotAsync();

        Assert.NotEqual(before.ActivationId, after.ActivationId);
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await DeliverAsync(receiver, envelope));
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResendsWithAnySentAt_AreDuplicatesWhichKeepTheAcceptedSentAt(bool completed)
    {
        var receiver = NewGrain();
        const string route = "messages/sent-time-retention";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var message = CreateEnvelope(receiver, NewMessage(32, "retained-command"), route);
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, message));
        await handler.WaitUntilEnteredAsync();
        if (completed)
        {
            handler.Release();
            await Fixture.WaitForInboxCountAsync(receiver, 0);
        }

        await receiver.RetryWriteStateAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var inbox = Fixture.GetInbox(receiver);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // A re-send is the same message, so its record keeps the time the inbox accepted.
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await DeliverAsync(receiver, message));
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await DeliverAsync(receiver, message with { SentAt = Fixture.JobNow }));
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await DeliverAsync(receiver, message with { SentAt = message.SentAt.AddTicks(-1) }));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(message.SentAt, await Fixture.OnGrainAsync(receiver, () => completed
            ? InboxTestAccess.GetProcessedSentAt(inbox, message.Envelope.MessageId)
            : InboxTestAccess.GetPendingMessages(inbox)[message.Envelope.MessageId].Message.SentAt));

        handler.Release();
        await Fixture.WaitForInboxCountAsync(receiver, 0);
        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(state.Effects).Count);
        Assert.Equal(message.SentAt, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedSentAt(inbox, message.Envelope.MessageId)));
    }
    [Fact]
    public async Task SameMessageIdFromDistinctSenders_DeduplicatesOneReceiverCommand()
    {
        var receiver = NewGrain();
        var original = CreateEnvelope(receiver, NewMessage(14, "sender-scoped"));
        var other = TestApplicationProtocol.With(original, Sessions,
            sender: GrainId.Create("other-sender", "same-message-id"));

        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, original));
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await DeliverAsync(receiver, other));
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);

        var previous = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await DeliverAsync(receiver, original));
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await DeliverAsync(receiver, other));
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
            DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, envelope));
            DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(await DeliverAsync(receiver, envelope));
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

        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, first));
        await WaitForBarrierAsync(receiver, barrier);
        var secondDelivery = DeliverAsync(receiver, second);
        Assert.False(secondDelivery.IsCompleted);

        barrier.Release();
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await secondDelivery);
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
