using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// Outbox sources and inbox sinks keep application-owned payloads as the only journaled copy.
/// </summary>
public abstract class MessageSourceAndSinkTests(MessageSourceAndSinkFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private InProcessTestCluster Cluster => fixture.Cluster;
    private RecordingJournalStorageProvider Storage => fixture.Storage;
    private SourceSinkProbe Probe => fixture.Probe;

    [Fact]
    public async Task SourceMessage_JournalsEachPayloadOnceAndRetiresInTheRemovalWrite()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var messageId = NewMessageId("delivered");
        var payload = NewPayload();

        await sender.StageFromSourceAsync(receiver.GetGrainId(), messageId, payload, write: true);

        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Writes(sender).Length == 2);
        var senderState = await OnGrainAsync(sender, context => Grain(context).GetState());
        Assert.Empty(senderState.Outgoing);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), Assert.Single(senderState.Retired).Value);
        Assert.Empty(await OnGrainAsync(sender, context => Grain(context).GetRetirementReasons()));
        // The outbox materializes the message once to validate the send and once for the delivery attempt.
        Assert.Equal(2, Probe.For(sender).Materializations);

        // The payload is journaled once by each grain: in the sender's source state and in the receiver's sink state.
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(sender), payload));
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(receiver), payload));

        // The retirement commits in the write which removes the outbox entry.
        var writes = Writes(sender);
        var retirement = Encoding.UTF8.GetBytes(nameof(OutboxMessageRetirementStatus.Delivered));
        Assert.True(writes[0].AsSpan().IndexOf(retirement) < 0);
        Assert.True(writes[1].AsSpan().IndexOf(retirement) >= 0);

        // The sink accepted the message without an inbox entry; the inbox journals only its processed record.
        var receiverState = await OnGrainAsync(receiver, context => (
            Grain(context).GetState(),
            Inbox(context).Count,
            InboxTestAccess.GetProcessedKeys(Inbox(context)).Contains(messageId)));
        Assert.Equal(payload, receiverState.Item1.Received[messageId.ToString()]);
        Assert.Equal(0, receiverState.Count);
        Assert.True(receiverState.Item3);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, Probe.For(receiver).Handled);
        Assert.Single(Writes(receiver));

        // A recovered sender agrees: the entry is removed and its retirement is recorded.
        Assert.True(Cluster.TryGetGrainContext(sender.GetGrainId(), out var senderContext));
        await sender.DeactivateAsync();
        await senderContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
        var recovered = await OnGrainAsync(sender, context => Grain(context).GetState());
        Assert.Empty(recovered.Outgoing);
        Assert.Single(recovered.Retired);
        Assert.Equal(2, Writes(sender).Length);
    }

    [Fact]
    public async Task SourceMessage_RecoveredFromTheJournal_IsDeliveredFromItsSource()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var messageId = NewMessageId("recovered");
        var payload = NewPayload();

        await sender.StageFromSourceAndDeactivateAsync(receiver.GetGrainId(), messageId, payload);

        await WaitUntilAsync(receiver, context => Grain(context).GetState().Received.ContainsKey(messageId.ToString()));
        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 1);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(sender), payload));
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(receiver), payload));
    }

    [Fact]
    public async Task SourceMessage_DeadLetteredByTheOutbox_MaterializesItsMessageBeforeRetiring()
    {
        var sender = NewGrain();
        var receiver = Cluster.Client.GetGrain<INoHandlerGrain>(Guid.NewGuid());
        await receiver.PingAsync();
        var messageId = NewMessageId("outbox-dead-letter");
        var payload = NewPayload();

        await sender.StageFromSourceAsync(receiver.GetGrainId(), messageId, payload, write: true);

        // The receiver has no handler, so delivery attempts fail until OutboxRetryTimeout elapses.
        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 1);
        var (state, deadLetters) = await OnGrainAsync(sender, context => (
            Grain(context).GetState(),
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().OutboxDeadLetters));
        Assert.Empty(state.Outgoing);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.DeadLettered), state.Retired[messageId.ToString()]);
        var letter = Assert.Single(deadLetters);
        Assert.Equal(letter.Reason, (await OnGrainAsync(sender, context => Grain(context).GetRetirementReasons()))[messageId.ToString()]);
        Assert.Equal(messageId, letter.Message.Envelope.MessageId);
        Assert.Equal(receiver.GetGrainId(), letter.Message.ReceiverId);
        Assert.Equal(payload, letter.Message.Envelope.Payload);
        Assert.Equal(SourceSinkGrain.Subject, letter.Message.Envelope.Subject);
        Assert.True(letter.AttemptCount > 1);
        // Each attempt materializes the message, as does a final wake which finds the retry timeout expired.
        Assert.InRange(Probe.For(sender).Materializations, letter.AttemptCount + 1, letter.AttemptCount + 2);

        // The dead letter keeps its own copy for diagnosis.
        await WaitUntilAsync(sender, _ => Storage.CountPayloadOccurrences(Journal(sender), payload) == 2);
    }

    [Fact]
    public async Task SourceMessage_DeadLetteredByTheReceiver_RetiresAsRejectedWithTheReceiversReasonAndCode()
    {
        const string code = SourceSinkGrain.RejectionCode;
        var sender = NewGrain();
        var receiver = NewGrain();
        Probe.For(receiver).Mode = SinkMode.DeadLetter;
        var messageId = NewMessageId("receiver-dead-letter");
        var payload = NewPayload();

        await sender.StageFromSourceAsync(receiver.GetGrainId(), messageId, payload, write: true);

        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 1);
        var (state, outboxDeadLetters) = await OnGrainAsync(sender, context => (
            Grain(context).GetState(),
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().OutboxDeadLetters));
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Rejected), state.Retired[messageId.ToString()]);
        Assert.Equal(SourceSinkGrain.Rejection, (await OnGrainAsync(sender, context => Grain(context).GetRetirementReasons()))[messageId.ToString()]);
        Assert.Equal(code, (await OnGrainAsync(sender, context => Grain(context).GetRetirementCodes())).GetValueOrDefault(messageId.ToString()));
        Assert.Empty(state.Outgoing);
        Assert.Empty(outboxDeadLetters);
        var inboxLetter = Assert.Single(await OnGrainAsync(receiver, context =>
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().InboxDeadLetters));
        Assert.Equal(payload, inboxLetter.Message.Envelope.Payload);
        Assert.Equal(SourceSinkGrain.Rejection, inboxLetter.Reason);
        Assert.Equal(code, inboxLetter.Code);
    }

    [Fact]
    public async Task SourceMessage_WhoseSinkThrowsTransiently_IsRetriedUntilTheSinkAcceptsIt()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        Probe.For(receiver).FailNextOffers(2);
        var messageId = NewMessageId("sink-transient-failure");
        var payload = NewPayload();

        await sender.StageFromSourceAsync(receiver.GetGrainId(), messageId, payload, write: true);

        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 1);
        var (state, outboxDeadLetters) = await OnGrainAsync(sender, context => (
            Grain(context).GetState(),
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().OutboxDeadLetters));
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), state.Retired[messageId.ToString()]);
        Assert.Empty(outboxDeadLetters);
        var (received, inboxDeadLetters) = await OnGrainAsync(receiver, context => (
            Grain(context).GetState().Received,
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().InboxDeadLetters));
        Assert.Equal(payload, received[messageId.ToString()]);
        Assert.Empty(inboxDeadLetters);
        Assert.Equal(3, Probe.For(receiver).SinkOffers);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
    }

    [Fact]
    public async Task SourceMessage_WhoseSinkKeepsThrowing_DeadLettersAtTheSenderWithTheLastErrorAfterTheRetryTimeout()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        Probe.For(receiver).Mode = SinkMode.Throw;
        var messageId = NewMessageId("sink-persistent-failure");

        await sender.StageFromSourceAsync(receiver.GetGrainId(), messageId, NewPayload(), write: true);

        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 1);
        var (state, outboxDeadLetters, reasons, codes) = await OnGrainAsync(sender, context => (
            Grain(context).GetState(),
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().OutboxDeadLetters,
            Grain(context).GetRetirementReasons(),
            Grain(context).GetRetirementCodes()));
        Assert.Equal(nameof(OutboxMessageRetirementStatus.DeadLettered), state.Retired[messageId.ToString()]);
        var letter = Assert.Single(outboxDeadLetters);
        Assert.Contains(SourceSinkGrain.SinkFailure, letter.Reason, StringComparison.Ordinal);
        Assert.Equal(letter.Reason, reasons[messageId.ToString()]);
        Assert.Null(letter.Code);
        Assert.Empty(codes);
        Assert.True(letter.AttemptCount > 1);

        var (received, inboxDeadLetters, inboxCount) = await OnGrainAsync(receiver, context => (
            Grain(context).GetState().Received,
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().InboxDeadLetters,
            Inbox(context).Count));
        Assert.Empty(received);
        Assert.Empty(inboxDeadLetters);
        Assert.Equal(0, inboxCount);
        Assert.Equal(letter.AttemptCount, Probe.For(receiver).SinkOffers);
    }

    [Fact]
    public async Task SourceMessage_CoalescesWithItsPendingReferenceAndRejectsConflicts()
    {
        var sender = NewGrain();
        var receiver = NewGrain().GetGrainId();
        var messageId = NewMessageId("coalesced");
        var sentId = NewMessageId("sent");

        await sender.StageFromSourceAsync(receiver, messageId, NewPayload(), write: false);
        await sender.SendFromSourceAsync(messageId);
        Assert.Equal(1, await sender.GetOutboxCountAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(receiver, messageId, NewPayload(), write: false));
        await sender.SendAsync(receiver, sentId, NewPayload(), write: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendFromSourceAsync(sentId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendFromSourceAsync(NewMessageId("unprovided")));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.StageFromSourceAsync(sender.GetGrainId(), NewMessageId("self"), NewPayload(), write: false));
        Assert.Equal(2, await sender.GetOutboxCountAsync());

        await sender.WriteAsync();
        await WaitUntilAsync(sender, context => Outbox(context).Count == 0);

        // Materialized to validate the first send, to reject the self-addressed send, and for delivery; an unprovided
        // message is never materialized.
        Assert.Equal(3, Probe.For(sender).Materializations);
    }

    [Fact]
    public async Task SourceMessage_WithdrawnBySource_IsRemovedWithoutDeliveryOrRetirement()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var messageId = NewMessageId("withdrawn");

        await sender.StageFromSourceAsync(receiver.GetGrainId(), messageId, NewPayload(), write: false);
        await sender.WithdrawAsync(messageId);
        Assert.Equal(1, await sender.GetOutboxCountAsync());
        await sender.WriteAsync();

        // The removal commits in its own write, after the write which staged the entry.
        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Writes(sender).Length == 2);
        var state = await OnGrainAsync(sender, context => Grain(context).GetState());
        Assert.Empty(state.Outgoing);
        Assert.Empty(state.Retired);
        Assert.Equal(1, Probe.For(sender).Materializations);
        Assert.Equal(0, Probe.For(receiver).SinkOffers);

        Assert.True(Cluster.TryGetGrainContext(sender.GetGrainId(), out var senderContext));
        await sender.DeactivateAsync();
        await senderContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
    }

    [Theory]
    [InlineData(SourceFault.Throw)]
    [InlineData(SourceFault.InvalidMessage)]
    [InlineData(SourceFault.MismatchedId)]
    public async Task SourceFailureAfterSend_DeadLettersOnlyThatEntryAndKeepsTheGrainWorking(SourceFault fault)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var first = NewMessageId("sibling");
        var failing = NewMessageId("failing");
        var second = NewMessageId("sibling");
        await sender.StageFromSourceAsync(receiver.GetGrainId(), first, NewPayload(), write: false);
        await sender.StageFromSourceAsync(receiver.GetGrainId(), failing, NewPayload(), write: false);
        await sender.StageFromSourceAsync(receiver.GetGrainId(), second, NewPayload(), write: false);
        Assert.True(Cluster.TryGetGrainContext(sender.GetGrainId(), out var activation));
        using var metrics = new MessageMetricListener(activation.ActivationServices.GetRequiredService<OrleansInstruments>().Meter);

        // The source provided every message when it was sent, and fails to provide one for its delivery attempt.
        Probe.For(sender).Faults[failing] = fault;
        await sender.WriteAsync();

        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 3);
        var (state, deadLetters) = await OnGrainAsync(sender, context => (
            Grain(context).GetState(),
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().OutboxDeadLetters));
        Assert.Empty(state.Outgoing);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.DeadLettered), state.Retired[failing.ToString()]);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), state.Retired[first.ToString()]);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), state.Retired[second.ToString()]);
        var received = (await OnGrainAsync(receiver, context => Grain(context).GetState())).Received;
        Assert.Equal(new[] { first.ToString(), second.ToString() }.Order(), received.Keys.Order());
        AssertSourceFailureDeadLetter(Assert.Single(deadLetters), failing, fault);
        Assert.Equal(
            Assert.Single(deadLetters).Reason,
            Assert.Single(await OnGrainAsync(sender, context => Grain(context).GetRetirementReasons())).Value);

        // The dead letter and its retirement commit in one write, before the write which retires the delivered siblings.
        var writes = Writes(sender);
        Assert.Equal(3, writes.Length);
        var deadLettered = Encoding.UTF8.GetBytes(nameof(OutboxMessageRetirementStatus.DeadLettered));
        var delivered = Encoding.UTF8.GetBytes(nameof(OutboxMessageRetirementStatus.Delivered));
        Assert.True(writes[1].AsSpan().IndexOf(deadLettered) >= 0);
        Assert.True(writes[1].AsSpan().IndexOf(delivered) < 0);
        Assert.True(writes[2].AsSpan().IndexOf(deadLettered) < 0);
        Assert.True(writes[2].AsSpan().IndexOf(delivered) >= 0);

        var grainType = sender.GetGrainId().Type.ToString();
        var outcomes = metrics.Read("outbox-messages-delivered");
        Assert.Equal(3, outcomes.Length);
        Assert.Single(outcomes, row => row.Tags.Contains(new("status", "source_error"))).AssertCounter(grainType, "source_error");
        Assert.Equal(2, outcomes.Count(row => row.Tags.Contains(new("status", "accepted"))));

        // The activation keeps processing messages; the source was called once per send and once per delivery attempt.
        var later = NewMessageId("later");
        await sender.StageFromSourceAsync(receiver.GetGrainId(), later, NewPayload(), write: true);
        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 4);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), (await OnGrainAsync(sender, context => Grain(context).GetState())).Retired[later.ToString()]);
        Assert.True(Cluster.TryGetGrainContext(sender.GetGrainId(), out var current));
        Assert.Same(activation, current);
        Assert.Equal(1, Probe.For(sender).Activations);
        Assert.Equal(8, Probe.For(sender).Materializations);

        // The persistent fault never fails a later activation: the dead letter recovers and the source isn't called again.
        await sender.DeactivateAsync();
        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
        var recovered = await OnGrainAsync(sender, context =>
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().OutboxDeadLetters);
        AssertSourceFailureDeadLetter(Assert.Single(recovered), failing, fault);
        Assert.Equal(2, Probe.For(sender).Activations);
        Assert.Equal(8, Probe.For(sender).Materializations);
    }

    [Fact]
    public async Task SourceFailureOnRecoveredEntry_DeadLettersItAndRetiresItWithTheFailingSource()
    {
        var grain = NewGrain();
        var receiver = NewGrain().GetGrainId();
        _ = await grain.GetOutboxCountAsync();
        var messageId = NewMessageId("recovered-failure");
        Probe.For(grain).Faults[messageId] = SourceFault.Throw;

        // Replays a reference entry whose source can no longer provide it, then runs a delivery pass.
        await OnGrainAsync(grain, context =>
        {
            Grain(context).StoreOutgoing(receiver, messageId, NewPayload());
            var outbox = Outbox(context);
            ApplyReference(outbox, messageId);
            ((IStateMachine)outbox).OnRecoveryCompleted();
            var pump = context.ActivationServices.GetRequiredService<MessagingPump>();
            pump.EnsureStarted();
            pump.Signal();
            return true;
        });

        await WaitUntilAsync(grain, context => Outbox(context).Count == 0);
        var (state, deadLetters) = await OnGrainAsync(grain, context => (
            Grain(context).GetState(),
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().OutboxDeadLetters));
        Assert.Empty(state.Outgoing);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.DeadLettered), state.Retired[messageId.ToString()]);
        AssertSourceFailureDeadLetter(Assert.Single(deadLetters), messageId, SourceFault.Throw);

        // The activation keeps processing messages.
        var later = NewMessageId("after-recovered-failure");
        await grain.StageFromSourceAsync(receiver, later, NewPayload(), write: true);
        await WaitUntilAsync(grain, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 2);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), (await OnGrainAsync(grain, context => Grain(context).GetState())).Retired[later.ToString()]);
        Assert.Equal(1, Probe.For(grain).Activations);
    }

    [Theory]
    [InlineData(SourceFault.Throw)]
    [InlineData(SourceFault.InvalidMessage)]
    [InlineData(SourceFault.MismatchedId)]
    public async Task SourceFailureDuringSend_ThrowsToTheCallerAndStagesNothing(SourceFault fault)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var messageId = NewMessageId("send-failure");
        Probe.For(sender).Faults[messageId] = fault;

        var stage = sender.StageFromSourceAsync(receiver.GetGrainId(), messageId, NewPayload(), write: false);
        var error = fault == SourceFault.InvalidMessage
            ? await Assert.ThrowsAsync<ArgumentException>(() => stage)
            : (Exception)await Assert.ThrowsAsync<InvalidOperationException>(() => stage);
        Assert.Contains(ExpectedSourceError(fault), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await sender.GetOutboxCountAsync());

        var later = NewMessageId("after-send-failure");
        await sender.StageFromSourceAsync(receiver.GetGrainId(), later, NewPayload(), write: true);
        await WaitUntilAsync(sender, context => Outbox(context).Count == 0 && Grain(context).GetState().Retired.Count == 1);
        Assert.Empty(await OnGrainAsync(sender, context =>
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().OutboxDeadLetters));
        Assert.Equal(1, Probe.For(sender).Activations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredDeadLetterWithoutReceiver_IsValidOnlyWithTheSourceFailureShape(bool withPayload)
    {
        var grain = NewGrain();
        _ = await grain.GetOutboxCountAsync();
        var messageId = NewMessageId("recovered-dead-letter");

        // Replays a dead letter without a receiver, then validates the recovered state as recovery does.
        var recovery = OnGrainAsync(grain, context =>
        {
            var outbox = Outbox(context);
            ((IDurableValueCommandHandler<OutboxCommand>)outbox).ApplySet(new OutboxCommand
            {
                Kind = OutboxCommandKind.Delivery,
                MessageId = messageId,
                DeadLetter = new()
                {
                    Message = new() { Envelope = new() { MessageId = messageId, Payload = withPayload ? NewPayload() : [] }, ReceiverId = default },
                    DeadLetteredAt = DateTimeOffset.UtcNow,
                    Reason = "The outbox message source failed to provide the message.",
                    AttemptCount = 1
                }
            });
            ((IStateMachine)outbox).OnRecoveryCompleted();
            return outbox.DeadLetterState.Count;
        });

        if (withPayload)
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => recovery);
            Assert.Contains("receiver must not be default", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(1, await recovery);
        }
    }

    [Fact]
    public async Task RecoveredReferenceEntry_IsWithdrawnWhenNoSourceProvidesItAndFailsWithoutSources()
    {
        var grain = NewGrain();
        var withoutSources = Cluster.Client.GetGrain<INoHandlerGrain>(Guid.NewGuid());
        _ = await grain.GetOutboxCountAsync();
        await withoutSources.PingAsync();
        var unprovided = NewMessageId("unprovided");

        // Replays a reference entry, validates the recovered state as recovery does, then runs a delivery pass.
        await OnGrainAsync(grain, context =>
        {
            var outbox = Outbox(context);
            ApplyReference(outbox, unprovided);
            ((IStateMachine)outbox).OnRecoveryCompleted();
            var pump = context.ActivationServices.GetRequiredService<MessagingPump>();
            pump.EnsureStarted();
            pump.Signal();
            return true;
        });
        await WaitUntilAsync(grain, context => Outbox(context).Count == 0);
        Assert.Empty((await OnGrainAsync(grain, context => Grain(context).GetState())).Retired);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => OnGrainAsync(withoutSources, context =>
        {
            var outbox = Outbox(context);
            ApplyReference(outbox, unprovided);
            ((IStateMachine)outbox).OnRecoveryCompleted();
            return true;
        }));
        Assert.Contains("no outbox message source is registered", error.Message, StringComparison.Ordinal);
    }

    private static void ApplyReference(Outbox outbox, MessageId messageId) =>
        ((IDurableValueCommandHandler<OutboxCommand>)outbox).ApplySet(new OutboxCommand
        {
            Kind = OutboxCommandKind.Enqueue,
            Reference = new() { MessageId = messageId },
            State = new()
        });

    private static void AssertSourceFailureDeadLetter(DeadLetter<OutboxMessage> letter, MessageId messageId, SourceFault fault)
    {
        // No valid message exists, so the dead letter carries only the message ID.
        Assert.Equal(messageId, letter.Message.Envelope.MessageId);
        Assert.True(letter.Message.ReceiverId.IsDefault);
        Assert.Empty(letter.Message.Envelope.Payload);
        Assert.Null(letter.Message.Envelope.Subject);
        Assert.Null(letter.Message.Envelope.Headers);
        Assert.Null(letter.Message.Envelope.DeliverAt);
        Assert.StartsWith("The outbox message source failed to provide the message: ", letter.Reason, StringComparison.Ordinal);
        Assert.Contains(ExpectedSourceError(fault), letter.Reason, StringComparison.Ordinal);
        Assert.Equal(1, letter.AttemptCount);
    }

    private static string ExpectedSourceError(SourceFault fault) => fault switch
    {
        SourceFault.Throw => SourceSinkGrain.SourceFailure,
        SourceFault.InvalidMessage => "The payload must not be null.",
        _ => "The outbox message source returned message ID"
    };

    [Fact]
    public async Task SinkAcceptance_DeduplicatesWithinTheWindowAndTheSinkReportsLaterDuplicates()
    {
        var receiver = NewGrain();
        var message = NewInboxMessage("deduplicated", NewPayload());
        var extension = receiver.AsReference<IMessagingExtension>();

        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(1, Probe.For(receiver).SinkOffers);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, await OnGrainAsync(receiver, context => Inbox(context).Count));

        fixture.Clock.Advance(MessageSourceAndSinkFixture.DeduplicationWindow);
        Assert.Equal(DeliveryStatus.Duplicate, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(2, Probe.For(receiver).SinkOffers);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, Probe.For(receiver).Handled);
        Assert.Equal(0, await OnGrainAsync(receiver, context => Inbox(context).Count));
    }

    [Fact]
    public async Task SinkRejection_DeadLettersTheMessageAndAnswersItsDuplicatesWithTheSameRejection()
    {
        const string reason = SourceSinkGrain.Rejection;
        const string code = SourceSinkGrain.RejectionCode;
        var receiver = NewGrain();
        Probe.For(receiver).Mode = SinkMode.DeadLetter;
        var payload = NewPayload();
        var message = NewInboxMessage("rejected", payload);
        var extension = receiver.AsReference<IMessagingExtension>();
        await receiver.WriteAsync();
        using var metrics = new MessageMetricListener(await OnGrainAsync(receiver, context => context.ActivationServices.GetRequiredService<OrleansInstruments>().Meter));

        var result = await extension.DeliverAsync(message, Token);

        Assert.Equal(DeliveryStatus.DeadLettered, result.Status);
        Assert.Equal(reason, result.Message);
        Assert.Equal(code, result.Code);
        var grainType = receiver.GetGrainId().Type.ToString();
        Assert.Single(metrics.Read("inbox-messages-received")).AssertCounter(grainType, "dead_lettered");
        Assert.Single(metrics.Read("inbox-messages-processed")).AssertCounter(grainType, "dead_lettered");
        var (state, count, letters) = await OnGrainAsync(receiver, context => (
            Grain(context).GetState(),
            Inbox(context).Count,
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().InboxDeadLetters));
        Assert.Empty(state.Received);
        Assert.Equal(0, count);
        var letter = Assert.Single(letters);
        Assert.Equal(payload, letter.Message.Envelope.Payload);
        Assert.Equal(reason, letter.Reason);
        Assert.Equal(code, letter.Code);
        Assert.Equal(1, letter.AttemptCount);
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(receiver), payload));

        // A duplicate receives the rejection from the processed record, which outlives the dead letter, without
        // reaching the sink.
        Probe.For(receiver).Mode = SinkMode.Accept;
        AssertRejected(await extension.DeliverAsync(message, Token));
        Assert.True(await OnGrainAsync(receiver, context =>
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().RemoveInboxDeadLetter(message.Envelope.MessageId)));
        AssertRejected(await extension.DeliverAsync(message, Token));
        Assert.Equal(1, Probe.For(receiver).SinkOffers);
        Assert.Equal(0, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, Probe.For(receiver).Handled);

        // Once the processed record leaves the deduplication window, the message is offered to the sink again.
        fixture.Clock.Advance(MessageSourceAndSinkFixture.DeduplicationWindow);
        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(2, Probe.For(receiver).SinkOffers);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);

        static void AssertRejected(DeliveryResult duplicate)
        {
            Assert.Equal(DeliveryStatus.DeadLettered, duplicate.Status);
            Assert.Equal(reason, duplicate.Message);
            Assert.Equal(code, duplicate.Code);
        }
    }

    [Theory]
    [InlineData(SinkMode.Throw, SourceSinkGrain.SinkFailure)]
    [InlineData(SinkMode.UnsupportedStatus, "The inbox message sink reported the unsupported delivery status 'HandlerNotFound'.")]
    public async Task SinkFailure_FailsTheDeliveryWithoutStagingAndTheRetryIsAccepted(SinkMode mode, string error)
    {
        var receiver = NewGrain();
        Probe.For(receiver).Mode = mode;
        var payload = NewPayload();
        var message = NewInboxMessage("sink-failure", payload);
        var extension = receiver.AsReference<IMessagingExtension>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await extension.DeliverAsync(message, Token));

        Assert.Equal(error, exception.Message);
        var (state, count, letters) = await OnGrainAsync(receiver, context => (
            Grain(context).GetState(),
            Inbox(context).Count,
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().InboxDeadLetters));
        Assert.Empty(state.Received);
        Assert.Equal(0, count);
        Assert.Empty(letters);
        Assert.Equal(0, Storage.CountPayloadOccurrences(Journal(receiver), payload));

        Probe.For(receiver).Mode = SinkMode.Accept;
        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(2, Probe.For(receiver).SinkOffers);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(receiver), payload));
    }

    [Fact]
    public async Task SinkBackpressure_StagesNothingAndTheRetryIsAccepted()
    {
        var receiver = NewGrain();
        Probe.For(receiver).Mode = SinkMode.Backpressure;
        var payload = NewPayload();
        var message = NewInboxMessage("backpressured", payload);
        var extension = receiver.AsReference<IMessagingExtension>();

        Assert.Equal(DeliveryStatus.Backpressured, (await extension.DeliverAsync(message, Token)).Status);
        var (count, letters) = await OnGrainAsync(receiver, context => (
            Inbox(context).Count,
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().InboxDeadLetters));
        Assert.Equal(0, count);
        Assert.Empty(letters);
        Assert.Equal(0, Storage.CountPayloadOccurrences(Journal(receiver), payload));

        Probe.For(receiver).Mode = SinkMode.Accept;
        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(2, Probe.For(receiver).SinkOffers);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, Probe.For(receiver).Handled);
    }

    [Fact]
    public async Task ScheduledMessage_IsOfferedToTheSinkBeforeItIsDue()
    {
        var receiver = NewGrain();
        var extension = receiver.AsReference<IMessagingExtension>();
        var payload = NewPayload();
        var scheduled = NewInboxMessage("scheduled", payload, DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(scheduled, Token)).Status);

        var (state, count) = await OnGrainAsync(receiver, context => (Grain(context).GetState(), Inbox(context).Count));
        Assert.Equal(payload, state.Received[scheduled.Envelope.MessageId.ToString()]);
        Assert.Equal(0, count);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, Probe.For(receiver).Handled);
    }

    [Fact]
    public async Task DeclinedMessages_UseInboxAcceptanceAndHandlers()
    {
        var receiver = NewGrain();
        var extension = receiver.AsReference<IMessagingExtension>();
        Probe.For(receiver).Mode = SinkMode.Decline;

        var scheduled = NewInboxMessage("scheduled", NewPayload(), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(scheduled, Token)).Status);
        Assert.Equal(1, await OnGrainAsync(receiver, context => Inbox(context).Count));

        var declinedPayload = NewPayload();
        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(NewInboxMessage("declined", declinedPayload), Token)).Status);
        await WaitUntilAsync(receiver, _ => Probe.For(receiver).Handled == 1);
        Assert.Equal(2, Probe.For(receiver).SinkOffers);
        Assert.Equal(0, Probe.For(receiver).SinkAcceptances);
        Assert.Empty((await OnGrainAsync(receiver, context => Grain(context).GetState())).Received);
        Assert.Equal(1, await OnGrainAsync(receiver, context => Inbox(context).Count));
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(receiver), declinedPayload));
    }

    [Fact]
    public async Task SelfSentMessage_IsAcceptedByTheSinkInTheSendingWrite()
    {
        var grain = NewGrain();
        _ = await grain.GetOutboxCountAsync();
        var writes = Writes(grain).Length;
        var messageId = NewMessageId("self");
        var payload = NewPayload();

        await grain.SendAsync(grain.GetGrainId(), messageId, payload, write: true);

        var (state, inboxCount, outboxCount) = await OnGrainAsync(grain, context => (Grain(context).GetState(), Inbox(context).Count, Outbox(context).Count));
        Assert.Equal(payload, state.Received[messageId.ToString()]);
        Assert.Equal(0, inboxCount);
        Assert.Equal(0, outboxCount);
        Assert.Equal(1, Probe.For(grain).SinkAcceptances);
        Assert.Equal(writes + 1, Writes(grain).Length);
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(grain), payload));
    }

    [Theory]
    [InlineData(SinkMode.Backpressure)]
    [InlineData(SinkMode.Throw)]
    [InlineData(SinkMode.UnsupportedStatus)]
    public async Task SelfSentMessage_FailsTheSendWithoutStagingWhenTheSinkBackpressuresOrFails(SinkMode mode)
    {
        var grain = NewGrain();
        _ = await grain.GetOutboxCountAsync();
        Probe.For(grain).Mode = mode;
        var writes = Writes(grain).Length;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.SendAsync(grain.GetGrainId(), NewMessageId("self-backpressured"), NewPayload(), write: true));

        var (state, inboxCount, outboxCount) = await OnGrainAsync(grain, context => (Grain(context).GetState(), Inbox(context).Count, Outbox(context).Count));
        Assert.Empty(state.Received);
        Assert.Equal(0, inboxCount);
        Assert.Equal(0, outboxCount);
        Assert.Equal(writes, Writes(grain).Length);
    }

    private ISourceSinkGrain NewGrain() => Cluster.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());

    private static MessageId NewMessageId(string name) => MessageId.Create("source-sink", name, Guid.NewGuid().ToString("N"));

    private static byte[] NewPayload() => RandomNumberGenerator.GetBytes(48);

    private static InboxMessage NewInboxMessage(string name, byte[] payload, DateTimeOffset? deliverAt = null) => new()
    {
        Envelope = new Envelope { MessageId = NewMessageId(name), Payload = payload, Subject = SourceSinkGrain.Subject, DeliverAt = deliverAt }
    };

    private static JournalId Journal(IAddressable grain) => JournalId.FromGrainId(grain.GetGrainId());

    private byte[][] Writes(IAddressable grain) => Storage.GetWrites(Journal(grain));

    private static SourceSinkGrain Grain(IGrainContext context) => Assert.IsType<SourceSinkGrain>(context.GrainInstance);

    private static Inbox Inbox(IGrainContext context) => context.ActivationServices.GetRequiredService<Inbox>();

    private static Outbox Outbox(IGrainContext context) => context.ActivationServices.GetRequiredService<Outbox>();

    private Task<T> OnGrainAsync<T>(IAddressable grain, Func<IGrainContext, T> func)
    {
        if (!Cluster.TryGetGrainContext(grain.GetGrainId(), out var context))
        {
            throw new InvalidOperationException($"Grain '{grain.GetGrainId()}' is not active.");
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try
            {
                done.SetResult(func(context));
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    private async Task WaitUntilAsync(IAddressable grain, Func<IGrainContext, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!Cluster.TryGetGrainContext(grain.GetGrainId(), out _) || !await OnGrainAsync(grain, predicate))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition was not met for grain '{grain.GetGrainId()}'.");
            }

            await Task.Delay(10, Token);
        }
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class JsonMessageSourceAndSinkTests(JsonMessageSourceAndSinkTests.Fixture fixture)
    : MessageSourceAndSinkTests(fixture), IClassFixture<JsonMessageSourceAndSinkTests.Fixture>
{
    public sealed class Fixture() : MessageSourceAndSinkFixture("json");
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class BinaryMessageSourceAndSinkTests(BinaryMessageSourceAndSinkTests.Fixture fixture)
    : MessageSourceAndSinkTests(fixture), IClassFixture<BinaryMessageSourceAndSinkTests.Fixture>
{
    public sealed class Fixture() : MessageSourceAndSinkFixture("orleans-binary");
}

public abstract class MessageSourceAndSinkFixture(string journalFormatKey) : IAsyncLifetime
{
    public static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(10);

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public RecordingJournalStorageProvider Storage { get; } = new();
    public SourceSinkProbe Probe { get; } = new();
    public InProcessTestCluster Cluster { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddVolatileJournalStorage();
            silo.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
            silo.Services.Configure<JsonJournalOptions>(options => options.AddTypeInfoResolver(SourceSinkJsonContext.Default));
            silo.Services.AddSingleton<TimeProvider>(Clock);
            silo.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);
            silo.UseInMemoryDurableJobs();
            silo.AddMessaging(options =>
            {
                options.DeduplicationWindow = DeduplicationWindow;
                options.OutboxRetryTimeout = TimeSpan.FromSeconds(3);
                options.RetryInitialInterval = TimeSpan.FromMilliseconds(10);
            });
            silo.Services.AddSingleton(Probe);
            silo.Services.RemoveAll<IJournalStorageProvider>();
            silo.Services.RemoveAll<IJournalStorageCatalog>();
            silo.Services.AddSingleton<IJournalStorageProvider>(services =>
            {
                Storage.Configure(services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
                return Storage;
            });
            silo.Services.AddSingleton<IJournalStorageCatalog>(
                services => (IJournalStorageCatalog)services.GetRequiredService<IJournalStorageProvider>());
        });
        Cluster = builder.Build();
        await Cluster.DeployAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await Cluster.DisposeAsync();
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class SourceSinkJsonContext : JsonSerializerContext;

public enum SinkMode
{
    Accept,
    Decline,
    Throw,
    DeadLetter,
    Backpressure,
    UnsupportedStatus
}

/// <summary>How the source fails to provide a message which it stores.</summary>
public enum SourceFault
{
    Throw,
    InvalidMessage,
    MismatchedId
}

public sealed class SourceSinkProbe
{
    private readonly ConcurrentDictionary<GrainId, Counters> _counters = new();

    public Counters For(IAddressable grain) => For(grain.GetGrainId());

    public Counters For(GrainId grainId) => _counters.GetOrAdd(grainId, static _ => new());

    public sealed class Counters
    {
        private int _activations;
        private int _materializations;
        private int _sinkOffers;
        private int _sinkAcceptances;
        private int _handled;
        private int _failuresRemaining;

        public volatile SinkMode Mode;
        public ConcurrentDictionary<MessageId, SourceFault> Faults { get; } = new();
        public int Activations => Volatile.Read(ref _activations);
        public int Materializations => Volatile.Read(ref _materializations);
        public int SinkOffers => Volatile.Read(ref _sinkOffers);
        public int SinkAcceptances => Volatile.Read(ref _sinkAcceptances);
        public int Handled => Volatile.Read(ref _handled);

        public void OnActivated() => Interlocked.Increment(ref _activations);
        public void OnMaterialized() => Interlocked.Increment(ref _materializations);
        public void OnSinkOffered() => Interlocked.Increment(ref _sinkOffers);
        public void OnSinkAccepted() => Interlocked.Increment(ref _sinkAcceptances);
        public void OnHandled() => Interlocked.Increment(ref _handled);

        /// <summary>Makes the sink throw for the next <paramref name="count"/> offers before applying <see cref="Mode"/>.</summary>
        public void FailNextOffers(int count) => Volatile.Write(ref _failuresRemaining, count);

        public bool TryConsumeFailure() => Interlocked.Decrement(ref _failuresRemaining) >= 0;
    }
}

public interface ISourceSinkGrain : IGrainWithGuidKey
{
    Task StageFromSourceAsync(GrainId receiver, MessageId messageId, byte[] payload, bool write);
    Task StageFromSourceAndDeactivateAsync(GrainId receiver, MessageId messageId, byte[] payload);
    Task SendFromSourceAsync(MessageId messageId);
    Task WithdrawAsync(MessageId messageId);
    Task SendAsync(GrainId receiver, MessageId messageId, byte[] payload, bool write);
    Task WriteAsync();
    Task<int> GetOutboxCountAsync();
    Task DeactivateAsync();
}

/// <summary>Stores outgoing messages as an outbox source and received payloads as an inbox sink.</summary>
public sealed class SourceSinkGrain : Grain, ISourceSinkGrain, IMessagingGrain, IOutboxMessageSource, IInboxMessageSink
{
    internal const string Subject = "source-sink/payload";
    internal const string Rejection = "The sink rejected the message.";
    internal const string RejectionCode = "source-sink/rejected";
    internal const string SinkFailure = "The sink failed before deciding the message's outcome.";
    internal const string SourceFailure = "The source can't decode the stored message.";
    private readonly IJournaledStateManager _manager;
    private readonly IOutbox _outbox;
    private readonly IDurableDictionary<string, byte[]> _outgoing;
    private readonly IDurableDictionary<string, string> _receivers;
    private readonly IDurableDictionary<string, byte[]> _received;
    private readonly IDurableDictionary<string, string> _retired;
    private readonly IDurableDictionary<string, string> _reasons;
    private readonly IDurableDictionary<string, string> _codes;
    private readonly SourceSinkProbe.Counters _probe;

    public SourceSinkGrain(
        IGrainContext context,
        IJournaledStateManager manager,
        IInbox inbox,
        IOutbox outbox,
        [FromKeyedServices("outgoing")] IDurableDictionary<string, byte[]> outgoing,
        [FromKeyedServices("receivers")] IDurableDictionary<string, string> receivers,
        [FromKeyedServices("received")] IDurableDictionary<string, byte[]> received,
        [FromKeyedServices("retired")] IDurableDictionary<string, string> retired,
        [FromKeyedServices("reasons")] IDurableDictionary<string, string> reasons,
        [FromKeyedServices("codes")] IDurableDictionary<string, string> codes,
        SourceSinkProbe probe)
    {
        _manager = manager;
        _outbox = outbox;
        _outgoing = outgoing;
        _receivers = receivers;
        _received = received;
        _retired = retired;
        _reasons = reasons;
        _codes = codes;
        _probe = probe.For(context.GrainId);
        _probe.OnActivated();
        outbox.Register(this);
        inbox.Register(this).Register(new Handler(_probe));
    }

    internal (Dictionary<string, byte[]> Outgoing, Dictionary<string, byte[]> Received, Dictionary<string, string> Retired) GetState() =>
        (new(_outgoing), new(_received), new(_retired));

    /// <summary>Gets the reasons of this source's dead-lettered messages, by message ID.</summary>
    internal Dictionary<string, string> GetRetirementReasons() => new(_reasons);

    /// <summary>Gets the receivers' rejection codes for this source's rejected messages, by message ID.</summary>
    internal Dictionary<string, string> GetRetirementCodes() => new(_codes);

    /// <summary>Stores an outgoing message in the source's state without sending it.</summary>
    internal void StoreOutgoing(GrainId receiver, MessageId messageId, byte[] payload)
    {
        _outgoing[messageId.ToString()] = payload;
        _receivers[messageId.ToString()] = receiver.ToString();
    }

    public async Task StageFromSourceAsync(GrainId receiver, MessageId messageId, byte[] payload, bool write)
    {
        _outgoing[messageId.ToString()] = payload;
        _receivers[messageId.ToString()] = receiver.ToString();
        _outbox.Send(messageId);
        if (write) await _manager.WriteStateAsync(CancellationToken.None);
    }

    public async Task StageFromSourceAndDeactivateAsync(GrainId receiver, MessageId messageId, byte[] payload)
    {
        await StageFromSourceAsync(receiver, messageId, payload, write: true);
        DeactivateOnIdle();
    }

    public Task SendFromSourceAsync(MessageId messageId)
    {
        _outbox.Send(messageId);
        return Task.CompletedTask;
    }

    public Task WithdrawAsync(MessageId messageId)
    {
        Assert.True(_outgoing.Remove(messageId.ToString()));
        Assert.True(_receivers.Remove(messageId.ToString()));
        return Task.CompletedTask;
    }

    public async Task SendAsync(GrainId receiver, MessageId messageId, byte[] payload, bool write)
    {
        _outbox.Send(new OutboxMessage
        {
            Envelope = new Envelope { MessageId = messageId, Payload = payload, Subject = Subject },
            ReceiverId = receiver
        });
        if (write) await _manager.WriteStateAsync(CancellationToken.None);
    }

    public async Task WriteAsync() => await _manager.WriteStateAsync(CancellationToken.None);

    public Task<int> GetOutboxCountAsync() => Task.FromResult(_outbox.Count());

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    bool IOutboxMessageSource.TryGetMessage(MessageId messageId, out OutboxMessage message)
    {
        var key = messageId.ToString();
        if (!_outgoing.TryGetValue(key, out var payload))
        {
            message = default;
            return false;
        }

        _probe.OnMaterialized();
        message = new()
        {
            Envelope = new() { MessageId = messageId, Payload = payload, Subject = Subject },
            ReceiverId = GrainId.Parse(_receivers[key])
        };
        if (_probe.Faults.TryGetValue(messageId, out var fault))
        {
            message = fault switch
            {
                SourceFault.Throw => throw new InvalidOperationException(SourceFailure),
                SourceFault.InvalidMessage => message with { Envelope = message.Envelope with { Payload = null! } },
                _ => message with { Envelope = message.Envelope with { MessageId = new MessageId(messageId.Key.CreateChildKey("other")) } }
            };
        }

        return true;
    }

    void IOutboxMessageSource.OnRetired(MessageId messageId, OutboxMessageRetirement retirement)
    {
        var key = messageId.ToString();
        Assert.True(_outgoing.Remove(key));
        Assert.True(_receivers.Remove(key));
        _retired[key] = retirement.Status.ToString();
        if (retirement.Reason is { } reason) _reasons[key] = reason;
        if (retirement.Code is { } code) _codes[key] = code;
    }

    bool IInboxMessageSink.TryAccept(InboxMessage message, out DeliveryResult result)
    {
        result = default;
        if (message.Envelope.Subject != Subject) return false;
        _probe.OnSinkOffered();
        var key = message.Envelope.MessageId.ToString();
        if (_probe.TryConsumeFailure()) throw new InvalidOperationException(SinkFailure);
        switch (_probe.Mode)
        {
            case SinkMode.Decline:
                return false;
            case SinkMode.Throw:
                throw new InvalidOperationException(SinkFailure);
            case SinkMode.DeadLetter:
                result = DeliveryResult.DeadLettered(Rejection, RejectionCode);
                return true;
            case SinkMode.Backpressure:
                result = DeliveryResult.Backpressured();
                return true;
            case SinkMode.UnsupportedStatus:
                result = DeliveryResult.HandlerNotFound();
                return true;
        }

        if (_received.ContainsKey(key))
        {
            result = DeliveryResult.Duplicate();
            return true;
        }

        _received[key] = message.Envelope.Payload;
        _probe.OnSinkAccepted();
        result = DeliveryResult.Accepted();
        return true;
    }

    private sealed class Handler(SourceSinkProbe.Counters probe) : IInboxHandler
    {
        public bool CanHandle(InboxMessage message) => message.Envelope.Subject == Subject;

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            probe.OnHandled();
            context.Complete();
            return ValueTask.CompletedTask;
        }
    }
}

public interface INoHandlerGrain : IGrainWithGuidKey
{
    Task PingAsync();
}

/// <summary>A messaging grain without handlers or sinks, whose inbox reports every delivery as handler-not-found.</summary>
public sealed class NoHandlerGrain : Grain, INoHandlerGrain, IMessagingGrain
{
    public Task PingAsync() => Task.CompletedTask;
}
