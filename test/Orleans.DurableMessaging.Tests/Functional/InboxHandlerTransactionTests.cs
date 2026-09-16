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
public sealed class InboxHandlerTransactionTests : DurableMessagingBehaviorTestBase
{
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
    public async Task HandlerFailure_RollsBackEffectCompletionAndOutgoingThenDeadLetters()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(Guid.NewGuid(), 9, "rollback", sink.GetGrainId(), ThrowAfterStaging: true));

        var accepted = await DeliverAsync(receiver, envelope.Value);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

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
    public async Task HandlerSelectionFailure_IsDeadLettered()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(80, "selection-failure"),
            "messages/selection-failure");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Empty(state.Effects);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Contains("Injected handler selection failure", deadLetter.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlerSelection_CannotStageOutboundMessages()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(81, "selection-mutation"),
            "messages/selection-mutation");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Empty(state.Effects);
        Assert.Equal(0, state.OutboxCount);
        Assert.Empty(state.OutboxDeadLetters);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Contains("selection is read-only", deadLetter.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandlerSelectionWriteAttemptRevertsBeforeRejectingDelivery()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(12, "can-handle-write"),
            "messages/can-handle-write");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DeliverAsync(receiver, envelope.Value));
        await receiver.RequestDeactivationAsync();
        var snapshot = await receiver.GetSnapshotAsync();

        Assert.Contains("mutate journaled state", exception.Message, StringComparison.Ordinal);
        Assert.Null(snapshot.InboxJobId);
        Assert.Equal(0, snapshot.InboxCount);
        Assert.Empty(snapshot.Effects);
    }

    [Fact]
    public async Task HandlerCannotCommitBeforeInboxCompletion()
    {
        var receiver = NewGrain();
        var message = new DurableTestMessage(
            Guid.NewGuid(),
            10,
            "premature-commit",
            CommitDuringHandling: true);
        using var envelope = CreateEnvelope(receiver, message);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Empty(state.Effects);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Contains("cannot be committed", deadLetter.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlerCannotDeleteStateBeforeInboxCompletion()
    {
        var receiver = NewGrain();
        var message = new DurableTestMessage(
            Guid.NewGuid(),
            11,
            "premature-delete",
            DeleteDuringHandling: true);
        using var envelope = CreateEnvelope(receiver, message);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Empty(state.Effects);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Contains("cannot be committed or deleted", deadLetter.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryDuringHandler_LeavesMessageRetryable()
    {
        var receiver = NewGrain();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/recover-handler");
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(78, "recover-handler"),
            "messages/recover-handler");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        await Fixture.RevertStateAsync(receiver);
        handler.Release();

        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task RecoveryDuringHandlerFailure_DiscardsStaleFailureAccounting()
    {
        var receiver = NewGrain();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/recover-handler-failure");
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(79, "recover-handler-failure") with { ThrowOnceAfterStaging = true },
            "messages/recover-handler-failure");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        await Fixture.RevertStateAsync(receiver);
        handler.Release();

        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task NullBodyAndContext_DecodeSuccessfullyAndTypedHandlersReceiveNull()
    {
        var receiver = NewGrain();
        using var referenceEnvelope = CreateEnvelope<string?>(
            receiver,
            body: null,
            route: "nullable/reference",
            builder => builder
                .WithContextValue<string?>("null-reference", null)
                .WithContextValue<int?>("null-value", null));

        Assert.True(referenceEnvelope.Value.Data.TryGetBody<string?>(out var referenceBody));
        Assert.Null(referenceBody);
        Assert.True(referenceEnvelope.Value.Data.TryGetContextValue<string?>("null-reference", out var referenceContext));
        Assert.Null(referenceContext);
        Assert.True(referenceEnvelope.Value.Data.TryGetContextValue<int?>("null-value", out var valueContext));
        Assert.Null(valueContext);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, referenceEnvelope.Value)).Status);

        using var valueEnvelope = CreateEnvelope<int?>(receiver, body: null, route: "nullable/value");
        Assert.True(valueEnvelope.Value.Data.TryGetBody<int?>(out var valueBody));
        Assert.Null(valueBody);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, valueEnvelope.Value)).Status);

        var completed = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.NullReferenceMessageCalls == 1
                && snapshot.NullNullableValueMessageCalls == 1);
        Assert.Equal(1, completed.NullReferenceMessageCalls);
        Assert.Equal(1, completed.NullNullableValueMessageCalls);
    }
}
