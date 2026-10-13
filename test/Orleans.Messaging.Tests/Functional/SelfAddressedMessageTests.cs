using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// Messages a grain sends to itself are accepted by its own inbox in the caller's journal write, without an outbox entry.
/// </summary>
[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class SelfAddressedMessageTests() : MessagingBehaviorTestBase(new SelfAddressedClusterFixture())
{
    private static readonly TimeSpan Delay = TimeSpan.FromHours(1);

    private ManualJobClock JobClock => ((SelfAddressedClusterFixture)Fixture).JobClock;

    [Fact]
    public async Task SelfSend_IsAcceptedWithoutOutboxEntryInTheCallersSingleWrite()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var instance = GetInstance(grain);
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        const string route = "messages/self-single-write";
        using var held = Fixture.HandlerProbe.Arm(grain.GetGrainId(), route);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        var effect = new MessageEffect(TestApplicationProtocol.NewMessageId(), 1, 0, "caller-state");

        await grain.StageEffectAsync(effect);
        await grain.StageOutputAsync(SelfMessage(grain, NewMessage(1, "self"), route));
        var staged = await grain.GetSnapshotAsync();
        Assert.Equal(1, staged.InboxCount);
        Assert.Equal(0, staged.OutboxCount);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));

        instance.ClearCaptures();
        await grain.RetryWriteStateAsync();
        var written = Assert.Single(instance.Captures);
        Assert.Equal(1, written.InboxCount);
        Assert.Equal(0, written.OutboxCount);
        Assert.Equal(effect, Assert.Single(written.Effects));
        Assert.Empty(instance.OutputCaptures[^1]);

        // Processing starts after that one write; the handler is held before it stages any further write.
        await WaitForBarrierAsync(grain, held);
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        held.Release();
        var completed = await Fixture.WaitForEffectCountAsync(grain, 2);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(writes + 2, Fixture.Storage.GetSuccessfulWriteCount(journal));
    }

    [Fact]
    public async Task SelfSend_IsProcessedOnlyAfterItsWriteIsDurable()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var instance = GetInstance(grain);
        var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(grain.GetGrainId()));

        var send = grain.SendAsync(grain.GetGrainId(), "messages/self-blocked", NewMessage(2, "blocked"));
        await storage.WaitUntilEnteredAsync();

        // A full pump pass while the write is in flight leaves the accepted message pending.
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await Fixture.RunPumpAsync(grain)).Status);
        var pending = await Fixture.OnGrainAsync(grain, () => instance.GetSnapshotForTest());
        Assert.Equal(1, pending.InboxCount);
        Assert.Equal(0, pending.OutboxCount);
        Assert.Empty(pending.Effects);

        storage.Release();
        await send;
        var completed = await Fixture.WaitForEffectCountAsync(grain, 1);
        Assert.Equal("blocked", Assert.Single(completed.Effects).Value);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task SelfSend_IsDiscardedWhenItsWriteFails()
    {
        var grain = NewGrain();
        var initial = await grain.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(grain);
        Fixture.Storage.FailWrite(JournalId.FromGrainId(grain.GetGrainId()));

        await Assert.ThrowsAnyAsync<Exception>(() => grain.SendAsync(grain.GetGrainId(), "messages/self-failed", NewMessage(3, "failed")));

        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await grain.GetSnapshotAsync();
        Assert.NotEqual(initial.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(grain)).Status);
        var after = await grain.GetSnapshotAsync();
        Assert.Empty(after.Effects);
        Assert.Equal(0, after.ProcessedMessageCount);
    }

    [Fact]
    public async Task SelfSend_FollowsInboxIdentityRules()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        const string route = "messages/self-identity";
        var message = SelfMessage(grain, NewMessage(4, "identity"), route);
        var differentBody = SelfMessage(grain, NewMessage(5, "different"), route, messageId: message.Envelope.MessageId);
        var differentDeliverAt = WithDeliverAt(message, JobClock.GetUtcNow() + Delay);

        using (var held = Fixture.HandlerProbe.Arm(grain.GetGrainId(), route))
        {
            await grain.StageOutputAsync(message);
            await grain.StageOutputAsync(message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.StageOutputAsync(differentBody));
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.StageOutputAsync(differentDeliverAt));
            Assert.Equal(1, (await grain.GetSnapshotAsync()).InboxCount);

            // The outbox keys pending messages by ID, so the ID cannot address another grain while it is pending there.
            var other = SelfMessage(grain, NewMessage(6, "other"), route);
            var otherReceiver = NewGrain().GetGrainId();
            await grain.StageOutputAsync(new OutboxMessage { ReceiverId = otherReceiver, Envelope = other.Envelope });
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.StageOutputAsync(other));

            await grain.RetryWriteStateAsync();
            await WaitForBarrierAsync(grain, held);
        }

        var completed = await Fixture.WaitForEffectCountAsync(grain, 1);
        Assert.Equal(1, completed.ProcessedMessageCount);

        // A completed ID is a duplicate regardless of content while its completion record is retained.
        await grain.StageOutputAsync(message);
        await grain.StageOutputAsync(differentBody);
        var repeated = await grain.GetSnapshotAsync();
        Assert.Equal(0, repeated.InboxCount);
        await grain.RetryWriteStateAsync();
        _ = await Fixture.RunPumpAsync(grain);
        var final = await grain.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(final.Effects).Count);
        Assert.Empty(final.InboxDeadLetters);
    }

    [Fact]
    public async Task SelfSend_IsAdmittedBeyondCapacityAndWithoutHandlers()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        await grain.ConfigureHandlerAsync(false);
        await ReactivateAsync(grain);

        // MaxCapacity is 2 and no handler is registered; a synchronous send admits every message.
        for (var sequence = 10; sequence < 13; sequence++)
        {
            await grain.StageOutputAsync(SelfMessage(grain, NewMessage(sequence, $"self-{sequence}"), "messages/self-capacity"));
        }

        await grain.RetryWriteStateAsync();
        var admitted = await grain.GetSnapshotAsync();
        Assert.Equal(3, admitted.InboxCount);
        Assert.Equal(0, admitted.OutboxCount);

        // Unscheduled self-sent messages occupy capacity for deliveries from other grains.
        var external = CreateEnvelope(grain, NewMessage(20, "external"));
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(grain, external)).Status);
        Assert.NotEqual(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(grain)).Status);
        var waiting = await grain.GetSnapshotAsync();
        Assert.Equal(3, waiting.InboxCount);
        Assert.Empty(waiting.Effects);
        Assert.Empty(waiting.InboxDeadLetters);

        await grain.ConfigureHandlerAsync(true);
        await ReactivateAsync(grain);
        _ = await Fixture.RunPumpAsync(grain);
        var completed = await Fixture.WaitForEffectCountAsync(grain, 3);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(grain, external)).Status);
        await Fixture.WaitForEffectCountAsync(grain, 4);
    }

    [Fact]
    public async Task SelfSendFromHandler_IsProcessedInALaterBatch()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var instance = GetInstance(grain);
        instance.ClearCaptures();
        var message = NewMessage(30, "forwarding") with { ForwardTo = grain.GetGrainId() };

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(grain, CreateEnvelope(grain, message))).Status);

        var completed = await Fixture.WaitForEffectCountAsync(grain, 2);
        Assert.Equal(2, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Equal(2, completed.ProcessedMessageCount);

        // The batch which processed the original also accepted the forwarded message, which a later batch processed.
        var captures = instance.Captures;
        var original = Assert.Single(captures, static capture => capture.Effects.Sum(static effect => effect.Count) == 1);
        Assert.Equal(1, original.InboxCount);
        Assert.Equal(0, original.OutboxCount);
        Assert.Equal(1, original.ProcessedMessageCount);
        Assert.All(instance.OutputCaptures, static output => Assert.Empty(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledSelfSend_SurvivesRecoveryAndIsProcessedWhenDue(bool snapshot)
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        if (snapshot)
        {
            Fixture.Storage.RequestSnapshot(journal);
        }

        var deliverAt = JobClock.GetUtcNow() + Delay;
        var message = WithDeliverAt(SelfMessage(grain, NewMessage(40, "recovered"), "messages/self-recovered"), deliverAt);
        await grain.StageOutputAsync(message);
        await grain.RetryWriteStateAsync();

        await ReactivateAsync(grain);
        var recovered = await grain.GetSnapshotAsync();
        Assert.Equal(1, recovered.InboxCount);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Empty(recovered.Effects);
        var inbox = Fixture.GetInbox(grain);
        Assert.Equal(deliverAt, await Fixture.OnGrainAsync(grain,
            () => InboxTestAccess.GetPendingMessages(inbox)[message.Envelope.MessageId].Message.Envelope.DeliverAt));

        var held = await Fixture.RunPumpAsync(grain);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, held.Status);
        Assert.Equal(deliverAt, held.RescheduleTime);

        // The recovered pending message deduplicates a repeated send.
        await grain.StageOutputAsync(message);
        Assert.Equal(1, (await grain.GetSnapshotAsync()).InboxCount);

        JobClock.Advance(Delay);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(grain)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(grain, 1);
        Assert.Equal("recovered", Assert.Single(completed.Effects).Value);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(0, completed.OutboxCount);
    }

    [Fact]
    public async Task SelfSend_RecordsSendAndReceiptWithoutOutboxMetrics()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(grain).ActivationServices;
        using var probe = new MessageMetricListener(services.GetRequiredService<OrleansInstruments>().Meter);
        var grainType = grain.GetGrainId().Type.ToString();
        const string route = "messages/self-metrics";
        var message = SelfMessage(grain, NewMessage(50, "metrics"), route);

        using (var held = Fixture.HandlerProbe.Arm(grain.GetGrainId(), route))
        {
            await grain.StageOutputAsync(message);
            await grain.StageOutputAsync(message);
            await grain.RetryWriteStateAsync();
            await WaitForBarrierAsync(grain, held);
        }

        await Fixture.WaitForEffectCountAsync(grain, 1);
        var sent = probe.Read("outbox-messages-sent");
        Assert.Equal(2, sent.Length);
        Assert.All(sent, row => row.AssertCounter(grainType));
        var received = probe.Read("inbox-messages-received");
        Assert.Equal(2, received.Length);
        received[0].AssertCounter(grainType, "accepted");
        received[1].AssertCounter(grainType, "duplicate");
        Assert.Equal([1d, -1d], probe.Read("inbox-depth").Select(static row => row.Value));
        Assert.Empty(probe.Read("outbox-depth"));
        Assert.Empty(probe.Read("outbox-messages-delivered"));
        Assert.Empty(probe.Read("outbox-delivery-duration"));
    }

    [Fact]
    public async Task RecoveredOutboxEntryAddressedToItsOwnGrain_IsRejected()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var outbox = Fixture.GetGrainContext(grain).ActivationServices.GetRequiredService<Outbox>();
        var message = SelfMessage(grain, NewMessage(60, "outbox-entry"), "messages/self-outbox-entry");

        // Replays an outbox entry which a correct journal never contains, then validates the recovered state.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Fixture.OnGrainAsync(grain, () =>
        {
            ((IDurableValueCommandHandler<OutboxCommand>)outbox).ApplySet(new OutboxCommand
            {
                Kind = OutboxCommandKind.Enqueue,
                Message = message,
                State = new() { EnqueuedAt = Fixture.Clock.GetUtcNow() }
            });
            ((IStateMachine)outbox).OnRecoveryCompleted();
        }));
        Assert.Contains("addressed to its own grain", error.Message, StringComparison.Ordinal);
    }

    private MessagingTestGrain GetInstance(IMessagingTestGrain grain) =>
        Assert.IsType<MessagingTestGrain>(Fixture.GetGrainContext(grain).GrainInstance);

    private OutboxMessage SelfMessage(IMessagingTestGrain grain, TestMessage message, string route, MessageId messageId = default) =>
        TestApplicationProtocol.Create(Sessions, grain.GetGrainId(), grain.GetGrainId(), route, message, messageId);

    private static OutboxMessage WithDeliverAt(OutboxMessage message, DateTimeOffset? deliverAt) =>
        new() { ReceiverId = message.ReceiverId, Envelope = message.Envelope with { DeliverAt = deliverAt } };

    private sealed class SelfAddressedClusterFixture() : MessagingClusterFixture(1, receiverOnly: false)
    {
        public ManualJobClock JobClock { get; } = new(DateTimeOffset.UtcNow);

        protected override void ConfigureServices(IServiceCollection services) =>
            services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, JobClock);
    }
}
