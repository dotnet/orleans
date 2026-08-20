using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DurableMessagingClusterCollection
{
    public const string Name = "Durable messaging cluster";
}

[Collection(DurableMessagingClusterCollection.Name)]
[TestCategory("BVT"), TestCategory("Journaling")]
public sealed class PublicDurableMessagingBehaviorTests : IAsyncLifetime
{
    private readonly DurableMessagingClusterFixture fixture = new();

    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task Deliver_AcceptedOnlyAfterInboxAndStableJobOwnershipAreDurable()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var barrier = fixture.Storage.BlockWrite(journalId);
        using var envelope = CreateEnvelope(receiver, NewMessage(1, "durability"));

        var delivery = DeliverAsync(receiver, envelope.Value);
        await barrier.WaitUntilEnteredAsync();

        Assert.False(delivery.IsCompleted);
        var staged = await receiver.GetSnapshotAsync();
        Assert.Equal(1, staged.InboxCount);
        Assert.Empty(staged.Effects);

        barrier.Release();
        var result = await delivery;
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        DurableEndpointSnapshot completed;
        try
        {
            completed = await fixture.WaitForEffectCountAsync(receiver, 1);
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Accepted message did not drain. Inbox={snapshot.InboxCount}, effects={snapshot.Effects.Count}, deadLetters={snapshot.InboxDeadLetters.Count}, job={snapshot.InboxJobId}.",
                exception);
        }
        Assert.Single(completed.Effects);
        Assert.Equal(0, completed.InboxCount);
        Assert.True(fixture.Storage.GetSuccessfulWriteCount(journalId) >= 2);
    }

    [Fact]
    public async Task FailedInboxAcceptance_RevertsEnvelopeAndOrphanedJobCannotProcess()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var envelope = CreateEnvelope(receiver, NewMessage(2, "failed-acceptance"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => DeliverAsync(receiver, envelope.Value));

        var reverted = await receiver.GetSnapshotAsync();
        Assert.Equal(0, reverted.InboxCount);
        Assert.Empty(reverted.Effects);
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(reverted.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Empty(recovered.Effects);
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
        var receiverState = await fixture.WaitForEffectCountAsync(receiver, 1);
        var sinkState = await fixture.WaitForEffectCountAsync(sink, 1);
        receiverState = await fixture.WaitForOutboxCountAsync(receiver, 0);

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
    public async Task HandlerFailure_RollsBackEffectCompletionAndOutgoingThenDeadLetters()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(Guid.NewGuid(), 9, "rollback", sink.GetGrainId(), ThrowAfterStaging: true));

        var accepted = await DeliverAsync(receiver, envelope.Value);
        var state = await fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Equal(DeliveryStatus.Accepted, accepted.Status);
        Assert.Empty(state.Effects);
        Assert.Equal(0, state.InboxCount);
        Assert.Equal(0, state.OutboxCount);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Equal(envelope.Value.MessageId, deadLetter.MessageId);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.Contains("Injected handler failure", deadLetter.Reason, StringComparison.Ordinal);
        Assert.Empty((await sink.GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task ConcurrentDuplicateDeliveries_ConvergeToOneEffectWithinRetention()
    {
        var receiver = NewGrain();
        using var barrier = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/blocked-duplicate");
        using var envelope = CreateEnvelope(receiver, NewMessage(11, "duplicate"), "messages/blocked-duplicate");

        var first = await DeliverAsync(receiver, envelope.Value);
        await barrier.WaitUntilEnteredAsync();
        var second = DeliverAsync(receiver, envelope.Value);

        Assert.Equal(DeliveryStatus.Accepted, first.Status);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

        barrier.Release();
        Assert.Equal(DeliveryStatus.Duplicate, (await second).Status);
        var state = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(state.Effects).Count);
        Assert.Equal(1, state.MaxConcurrentHandlers);
    }

    [Fact]
    public async Task DuplicateAfterReactivationWithinRetention_RemainsEffectivelyOnce()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(13, "reactivation"));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var before = await fixture.WaitForEffectCountAsync(receiver, 1);
        await receiver.RequestDeactivationAsync();
        var after = await receiver.GetSnapshotAsync();

        Assert.NotEqual(before.ActivationId, after.ActivationId);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
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
            using var envelope = CreateEnvelope(receiver, message);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        }

        var state = await fixture.WaitForEffectCountAsync(receiver, 3);
        Assert.Equal([1, 2, 3], state.Effects.Select(static effect => effect.Sequence));
        Assert.All(state.Effects, static effect => Assert.Equal(1, effect.Count));
    }

    [Fact]
    public async Task ConcurrentDelivery_WaitsWhileHandlersRemainSequential()
    {
        var receiver = NewGrain();
        using var barrier = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/sequential");
        using var first = CreateEnvelope(receiver, NewMessage(21, "first"), "messages/sequential");
        using var second = CreateEnvelope(receiver, NewMessage(22, "second"), "messages/sequential");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first.Value)).Status);
        await WaitForBarrierAsync(receiver, barrier);
        var secondDelivery = DeliverAsync(receiver, second.Value);
        Assert.False(secondDelivery.IsCompleted);

        barrier.Release();
        Assert.Equal(DeliveryStatus.Accepted, (await secondDelivery).Status);
        DurableEndpointSnapshot state;
        try
        {
            state = await fixture.WaitForEffectCountAsync(receiver, 2);
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

    [Fact]
    public async Task MalformedTypedBody_DeadLettersAndDoesNotBlockLaterValidMessage()
    {
        var receiver = NewGrain();
        using var malformed = CreateEnvelope(receiver, "wrong-body", "typed");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, malformed.Value)).Status);
        var poisoned = await fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.Empty(poisoned.Effects);
        var deadLetter = Assert.Single(poisoned.InboxDeadLetters);
        Assert.Equal(malformed.Value.MessageId, deadLetter.MessageId);
        Assert.Contains(nameof(DurableTestMessage), deadLetter.Reason, StringComparison.Ordinal);

        using var valid = CreateEnvelope(receiver, NewMessage(41, "valid-after-poison"), "typed");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, valid.Value)).Status);
        var recovered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("valid-after-poison", Assert.Single(recovered.Effects).Value);
        Assert.Single(recovered.InboxDeadLetters);
    }

    [Fact]
    public async Task StagedOutboxWithoutCommit_IsRemovedOnReactivationAndNeverDispatched()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var message = NewMessage(51, "uncommitted");

        await sender.StageWithoutCommitAsync(receiver.GetGrainId(), "messages/uncommitted", message);
        Assert.Equal(1, (await sender.GetSnapshotAsync()).OutboxCount);
        await sender.RequestDeactivationAsync();
        var reactivated = await sender.GetSnapshotAsync();

        Assert.Equal(0, reactivated.OutboxCount);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task FailedAtomicWrite_ReloadExposesNeitherGrainEffectNorOutgoingMessage()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        fixture.Storage.FailWrite(JournalId.FromGrainId(sender.GetGrainId()));

        await Assert.ThrowsAnyAsync<Exception>(
            () => sender.SendAsync(receiver.GetGrainId(), "messages/write-failure", NewMessage(53, "failed-write")));
        await sender.RequestDeactivationAsync();

        Assert.Equal(0, (await sender.GetSnapshotAsync()).OutboxCount);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task BlockedInboxHandler_DoesNotStopIndependentOutboxAndInboxPumps()
    {
        var blocked = NewGrain();
        var independentSender = NewGrain();
        var independentReceiver = NewGrain();
        using var barrier = fixture.HandlerProbe.Arm(blocked.GetGrainId(), "messages/blocked-pump");
        using var blockedEnvelope = CreateEnvelope(blocked, NewMessage(61, "blocked"), "messages/blocked-pump");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(blocked, blockedEnvelope.Value)).Status);
        await barrier.WaitUntilEnteredAsync();
        await independentSender.SendAsync(
            independentReceiver.GetGrainId(),
            "messages/independent",
            NewMessage(62, "independent"));
        var independent = await fixture.WaitForEffectCountAsync(independentReceiver, 1);

        Assert.Equal("independent", Assert.Single(independent.Effects).Value);
        Assert.Empty((await blocked.GetSnapshotAsync()).Effects);
        barrier.Release();
        Assert.Equal("blocked", Assert.Single((await fixture.WaitForEffectCountAsync(blocked, 1)).Effects).Value);
    }

    [Fact]
    public async Task RouteNotFound_IsRejectedWithoutInboxPersistence()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(71, "missing"), "unknown/route");

        var result = await DeliverAsync(receiver, envelope.Value);

        Assert.Equal(DeliveryStatus.RouteNotFound, result.Status);
        Assert.Equal("No handler for route 'unknown/route'", result.Message);
        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(0, state.InboxCount);
        Assert.Empty(state.Effects);
    }

    [Fact]
    public async Task RouteNotFound_OutboxRetriesThenDeadLettersWithoutReceiverPersistence()
    {
        var sender = NewGrain();
        var receiver = NewGrain();

        await sender.SendAsync(
            receiver.GetGrainId(),
            "unknown/outbox-route",
            NewMessage(72, "undeliverable"));
        var senderState = await fixture.WaitForDeadLetterCountAsync(sender, 1);

        Assert.Equal(0, senderState.OutboxCount);
        var deadLetter = Assert.Single(senderState.OutboxDeadLetters);
        Assert.Equal("unknown/outbox-route", deadLetter.Route);
        Assert.Equal(3, deadLetter.AttemptCount);
        Assert.Contains("No handler", deadLetter.Reason, StringComparison.Ordinal);
        var receiverState = await receiver.GetSnapshotAsync();
        Assert.Equal(0, receiverState.InboxCount);
        Assert.Empty(receiverState.Effects);
        Assert.Empty(receiverState.InboxDeadLetters);
    }

    private IDurableMessagingTestGrain NewGrain() =>
        fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());

    private static DurableTestMessage NewMessage(int sequence, string value) =>
        new(Guid.NewGuid(), sequence, value);

    private static async Task<DeliveryResult> DeliverAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope) =>
        await receiver.AsReference<IDurableInboxExtension>().DeliverAsync(envelope);

    private static async Task WaitForBarrierAsync(
        IDurableMessagingTestGrain receiver,
        HandlerProbe.Barrier barrier)
    {
        try
        {
            await barrier.WaitUntilEnteredAsync();
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Handler did not start. Inbox={snapshot.InboxCount}, effects={snapshot.Effects.Count}, maxHandlers={snapshot.MaxConcurrentHandlers}, deadLetters={string.Join(" | ", snapshot.InboxDeadLetters.Select(static item => item.Reason))}.",
                exception);
        }
    }

    private EnvelopeLease CreateEnvelope(
        IDurableMessagingTestGrain receiver,
        DurableTestMessage message,
        string route = "messages/record") =>
        CreateEnvelope(receiver, (object)message, route);

    private EnvelopeLease CreateEnvelope(
        IDurableMessagingTestGrain receiver,
        object body,
        string route)
    {
        var sessions = fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("external-test-sender", Guid.NewGuid().ToString("N"));
        var builder = new DurableEnvelopeBuilder(sessions, sender).To(receiver.GetGrainId(), route);
        var envelope = body switch
        {
            DurableTestMessage message => builder.WithBody(message).Build(),
            string text => builder.WithBody(text).Build(),
            _ => throw new ArgumentException($"Unsupported test body type {body.GetType()}.", nameof(body)),
        };
        return new EnvelopeLease(envelope);
    }

    private sealed class EnvelopeLease(DurableEnvelope value) : IDisposable
    {
        public DurableEnvelope Value { get; } = value;
        public void Dispose()
        {
        }
    }
}
