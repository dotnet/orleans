using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
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
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class PublicDurableMessagingBehaviorTests : IAsyncLifetime
{
    private readonly DurableMessagingClusterFixture fixture = new();

    public ValueTask InitializeAsync() => fixture.InitializeAsync();
    public ValueTask DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public void DefaultHosting_UsesBinaryJournalFormat()
    {
        Assert.Equal("orleans-binary", fixture.Storage.JournalFormatKey);
    }

    [Fact]
    public async Task ApplicationJournaledStateNamesDoNotCollideWithMessagingState()
    {
        var grain = NewGrain();

        var snapshot = await grain.GetSnapshotAsync();

        Assert.Equal(0, snapshot.InboxCount);
        Assert.Equal(0, snapshot.OutboxCount);
    }

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
        var staged = fixture.GetSnapshot(receiver);
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
    public async Task Deliver_CancellationStopsWaitingForInboxGate()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var barrier = fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var firstEnvelope = CreateEnvelope(receiver, NewMessage(72, "holds-gate"));
        using var secondEnvelope = CreateEnvelope(receiver, NewMessage(73, "canceled"));
        var firstDelivery = DeliverAsync(receiver, firstEnvelope.Value);
        await barrier.WaitUntilEnteredAsync();
        using var cancellation = new CancellationTokenSource();

        var canceledDelivery = DeliverWithCancellationAsync(
            receiver,
            secondEnvelope.Value,
            cancellation.Token);
        Assert.False(canceledDelivery.IsCompleted);
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledDelivery);
        }
        finally
        {
            barrier.Release();
        }

        Assert.Equal(DeliveryStatus.Accepted, (await firstDelivery).Status);
    }

    [Fact]
    public async Task ConcurrentWriteCannotCaptureInboxAcceptanceBeforeScheduling()
    {
        var receiver = NewGrain();
        using var schedule = fixture.JobManagerProbe.BlockNext("orleans.messaging.inbox-drain");
        using var envelope = CreateEnvelope(receiver, NewMessage(74, "schedule-barrier"));

        var delivery = DeliverAsync(receiver, envelope.Value);
        await schedule.WaitUntilEnteredAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.WriteStateAsync(receiver).AsTask());
        Assert.Contains("waiting for job scheduling", exception.Message, StringComparison.Ordinal);

        schedule.Continue();
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        var completed = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("schedule-barrier", Assert.Single(completed.Effects).Value);
    }

    [Fact]
    public async Task RecoveryDuringInboxScheduling_PreventsFalseAcceptance()
    {
        var receiver = NewGrain();
        using var schedule = fixture.JobManagerProbe.BlockNext("orleans.messaging.inbox-drain");
        using var envelope = CreateEnvelope(receiver, NewMessage(77, "recovered-during-schedule"));

        var delivery = DeliverAsync(receiver, envelope.Value);
        await schedule.WaitUntilEnteredAsync();
        await fixture.RevertStateAsync(receiver);
        schedule.Continue();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => delivery);
        Assert.Contains("interrupted by state recovery", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.GetSnapshot(receiver).InboxCount);
    }

    [Fact]
    public async Task FailedInboxAcceptance_RevertsEnvelopeAndOrphanedJobCannotProcess()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var depthBaseline = fixture.Metrics.GetDepth("orleans-durable-messaging-inbox-depth");
        fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var envelope = CreateEnvelope(receiver, NewMessage(2, "failed-acceptance"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => DeliverAsync(receiver, envelope.Value));

        var reverted = await receiver.GetSnapshotAsync();
        Assert.Equal(0, reverted.InboxCount);
        Assert.Empty(reverted.Effects);
        Assert.Equal(depthBaseline, fixture.Metrics.GetDepth("orleans-durable-messaging-inbox-depth"));
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(reverted.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Empty(recovered.Effects);
        Assert.Equal(depthBaseline, fixture.Metrics.GetDepth("orleans-durable-messaging-inbox-depth"));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(depthBaseline, fixture.Metrics.GetDepth("orleans-durable-messaging-inbox-depth"));
    }

    [Fact]
    public async Task AmbiguousInboxAcceptanceCommit_PreservesAndProcessesRecoveredEnvelope()
    {
        var receiver = NewGrain();
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        fixture.Storage.FailAfterWrite(journalId);
        using var envelope = CreateEnvelope(receiver, NewMessage(76, "ambiguous-acceptance"));

        await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope.Value));

        var completed = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("ambiguous-acceptance", Assert.Single(completed.Effects).Value);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task Inbox_PrecommitCrash_ReclaimsScheduledOrphanAfterRecovery()
    {
        const string jobName = "orleans.messaging.inbox-drain";
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        await receiver.DeactivateOnNextRecoveryAsync();
        var barrier = fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var attemptBaseline = fixture.Metrics.GetCount("orleans-durablejobs-job-attempts-started");
        var completionBaseline = fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var orphanBaseline = fixture.Metrics.GetCount(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            jobName);
        using var envelope = CreateEnvelope(receiver, NewMessage(3, "inbox-orphan"));

        var delivery = DeliverAsync(receiver, envelope.Value);
        await barrier.WaitUntilEnteredAsync();
        await fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-job-attempts-started",
            attemptBaseline + 1);

        Assert.Equal(
            orphanBaseline,
            fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
        barrier.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => delivery);

        var recovered = await fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId);
        await fixture.Metrics.WaitForCountAsync(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            orphanBaseline + 1,
            jobName);
        await fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-jobs-completed",
            completionBaseline + 1);

        Assert.Equal(0, recovered.InboxCount);
        Assert.Null(recovered.InboxJobId);
        Assert.Empty(recovered.Effects);
        Assert.Equal(
            orphanBaseline + 1,
            fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
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
    public async Task HandlerSelectionFailure_IsDeadLettered()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(80, "selection-failure"),
            "messages/selection-failure");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await fixture.WaitForDeadLetterCountAsync(receiver, 1);

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
        var state = await fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Empty(state.Effects);
        Assert.Equal(0, state.OutboxCount);
        Assert.Empty(state.OutboxDeadLetters);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Contains("selection is read-only", deadLetter.Reason, StringComparison.OrdinalIgnoreCase);
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
        var state = await fixture.WaitForDeadLetterCountAsync(receiver, 1);

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
        var state = await fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Empty(state.Effects);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Contains("cannot be committed or deleted", deadLetter.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryDuringHandler_LeavesMessageRetryable()
    {
        var receiver = NewGrain();
        using var handler = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/recover-handler");
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(78, "recover-handler"),
            "messages/recover-handler");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        await fixture.RevertStateAsync(receiver);
        handler.Release();

        var completed = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task RecoveryDuringHandlerFailure_DiscardsStaleFailureAccounting()
    {
        var receiver = NewGrain();
        using var handler = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/recover-handler-failure");
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(79, "recover-handler-failure") with { ThrowOnceAfterStaging = true },
            "messages/recover-handler-failure");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        await fixture.RevertStateAsync(receiver);
        handler.Release();

        var completed = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(0, completed.InboxCount);
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
        Assert.Equal(1, fixture.GetSnapshot(receiver).InboxCount);

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
    public async Task InboxDeadLetterRemoval_IsDurable()
    {
        var receiver = NewGrain();
        using var malformed = CreateEnvelope(receiver, "wrong-body", "typed");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, malformed.Value)).Status);
        _ = await fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.True(await receiver.RemoveInboxDeadLetterAsync(
            malformed.Value.SenderId,
            malformed.Value.MessageId));
        Assert.Empty((await receiver.GetSnapshotAsync()).InboxDeadLetters);
        Assert.False(await receiver.RemoveInboxDeadLetterAsync(
            malformed.Value.SenderId,
            malformed.Value.MessageId));

        await receiver.RequestDeactivationAsync();
        Assert.Empty((await receiver.GetSnapshotAsync()).InboxDeadLetters);
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
    public async Task CommittedOutbox_DeactivationBeforeLocalFollowUp_RecoversDurableJobOwnership()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAndDeactivateAsync(
            receiver.GetGrainId(),
            "messages/outbox-crash-window",
            NewMessage(52, "durable-wakeup"));
        await receiverWrite.WaitUntilEnteredAsync();
        var committed = fixture.GetSnapshot(sender);

        Assert.Equal(1, committed.OutboxCount);
        Assert.False(string.IsNullOrEmpty(committed.OutboxJobId));

        receiverWrite.Release();
        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);
        var recovered = await fixture.SnapshotProbe.WaitAsync(
            sender.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId
                && snapshot.OutboxCount == 0
                && snapshot.OutboxJobId is null);

        Assert.Equal("durable-wakeup", Assert.Single(delivered.Effects).Value);
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Null(recovered.OutboxJobId);
    }

    [Fact]
    public async Task DuplicateOutboxEnqueue_PersistsOneStableJobOwnership()
    {
        var sender = NewGrain();
        var receiver = NewGrain();

        await sender.SendDuplicateAsync(
            receiver.GetGrainId(),
            "messages/duplicate-outbox-enqueue",
            NewMessage(54, "duplicate-enqueue"));
        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);

        Assert.Equal(1, Assert.Single(delivered.Effects).Count);
        Assert.Equal(
            1,
            fixture.JobManagerProbe.GetSuccessCount(
                "orleans.messaging.outbox-flush",
                sender.GetGrainId()));
    }

    [Fact]
    public async Task OutboxSchedulingFailure_AbortsCommitAndRetryUsesStableOwnership()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        fixture.JobManagerProbe.FailAfterNext("orleans.messaging.outbox-flush");

        await Assert.ThrowsAsync<IOException>(
            () => sender.SendAsync(
                receiver.GetGrainId(),
                "messages/schedule-retry",
                NewMessage(55, "schedule-retry")));
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);

        await sender.RetryWriteStateAsync();
        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);

        var effect = Assert.Single(delivered.Effects);
        Assert.Equal("schedule-retry", effect.Value);
        Assert.Equal(1, effect.Count);
        Assert.Equal(
            2,
            fixture.JobManagerProbe.GetAttemptCount(
                "orleans.messaging.outbox-flush",
                sender.GetGrainId()));
        Assert.Equal(
            2,
            fixture.JobManagerProbe.GetSuccessCount(
                "orleans.messaging.outbox-flush",
                sender.GetGrainId()));
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
        Assert.Equal(
            1,
            fixture.JobManagerProbe.GetSuccessCount(
                "orleans.messaging.outbox-flush",
                sender.GetGrainId()));
    }

    [Fact]
    public async Task DeleteThenWrite_DiscardsPendingOutboxWithoutPoisoningNextWrite()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        await sender.StageWithoutCommitAsync(
            receiver.GetGrainId(),
            "messages/delete-then-write",
            NewMessage(75, "delete-then-write"));

        await sender.DeleteThenWriteStateAsync();

        Assert.Equal(0, (await sender.GetSnapshotAsync()).OutboxCount);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task Outbox_PrecommitCrash_ReclaimsScheduledOrphanAfterRecovery()
    {
        const string jobName = "orleans.messaging.outbox-flush";
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        await sender.DeactivateOnNextRecoveryAsync();
        var barrier = fixture.Storage.BlockWrite(JournalId.FromGrainId(sender.GetGrainId()));
        var attemptBaseline = fixture.Metrics.GetCount("orleans-durablejobs-job-attempts-started");
        var completionBaseline = fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var orphanBaseline = fixture.Metrics.GetCount(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            jobName);

        var send = sender.SendAsync(
            receiver.GetGrainId(),
            "messages/outbox-orphan",
            NewMessage(54, "outbox-orphan"));
        await barrier.WaitUntilEnteredAsync();
        await fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-job-attempts-started",
            attemptBaseline + 1);

        Assert.Equal(
            orphanBaseline,
            fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
        barrier.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => send);

        await sender.RequestDeactivationAsync();
        var recovered = await sender.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        await fixture.Metrics.WaitForCountAsync(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            orphanBaseline + 1,
            jobName);
        await fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-jobs-completed",
            completionBaseline + 1);

        Assert.Equal(0, recovered.OutboxCount);
        Assert.Null(recovered.OutboxJobId);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
        Assert.Equal(
            orphanBaseline + 1,
            fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
    }

    [Fact]
    public async Task OutboxJobClearWriteFailure_RevertsOwnershipAndRetryCleansUp()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var journalId = JournalId.FromGrainId(sender.GetGrainId());
        var writeBaseline = fixture.Storage.GetSuccessfulWriteCount(journalId);
        fixture.Storage.FailWrite(journalId, matchingWrite: 3);

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/outbox-clear-retry",
            NewMessage(56, "outbox-clear-retry"));
        _ = await fixture.WaitForEffectCountAsync(receiver, 1);
        await fixture.Storage.WaitForSuccessfulWriteCountAsync(journalId, writeBaseline + 3);
        var cleaned = await sender.GetSnapshotAsync();

        Assert.Equal(0, cleaned.OutboxCount);
        Assert.Null(cleaned.OutboxJobId);
        await sender.RequestDeactivationAsync();
        var recovered = await sender.GetSnapshotAsync();
        Assert.Null(recovered.OutboxJobId);
        Assert.Equal(0, recovered.OutboxCount);
    }

    [Fact]
    public async Task InboxJobClearWriteFailure_RevertsThenRecoversAfterActivationLoss()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        using var handler = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/inbox-clear-retry");
        using var envelope = CreateEnvelope(receiver, NewMessage(57, "inbox-clear-retry"), "messages/inbox-clear-retry");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()), matchingWrite: 2);
        fixture.DeactivateOnNextRecovery(receiver);
        handler.Release();

        var recovered = await fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId);
        var cleaned = await fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.InboxCount == 0 && snapshot.InboxJobId is null);

        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(cleaned.Effects).Count);
        Assert.Empty(cleaned.InboxDeadLetters);
        Assert.Null(cleaned.InboxJobId);
    }

    [Fact]
    public async Task DeliveryIntoEmptyInbox_ReplacesStalePersistedJobOwnership()
    {
        var receiver = NewGrain();
        var staleJobId = $"stale-{Guid.NewGuid():N}";
        await receiver.SetInboxJobIdAsync(staleJobId);
        using var handler = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/stale-owner");
        using var envelope = CreateEnvelope(receiver, NewMessage(58, "stale-owner"), "messages/stale-owner");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var accepted = fixture.GetSnapshot(receiver);

        Assert.NotNull(accepted.InboxJobId);
        Assert.NotEqual(staleJobId, accepted.InboxJobId);

        handler.Release();
        var completed = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("stale-owner", Assert.Single(completed.Effects).Value);
    }

    [Fact]
    public async Task Inbox_StaleGenerationCompletesWithoutClearingNewerOwner()
    {
        var receiver = NewGrain();
        const string route = "messages/stale-inbox-generation";
        using var handler = fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(60, "newer-inbox-owner"), route);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var owned = fixture.GetSnapshot(receiver);
        Assert.False(string.IsNullOrEmpty(owned.InboxJobId));
        var completionBaseline = fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var manager = fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILocalDurableJobManager>();

        await manager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = receiver.GetGrainId(),
                JobName = "orleans.messaging.inbox-drain",
                DueTime = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["orleans.messaging.ownership-id"] = "0"
                }
            },
            TestContext.Current.CancellationToken);
        await fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-jobs-completed",
            completionBaseline + 1);

        Assert.Equal(owned.InboxJobId, fixture.GetSnapshot(receiver).InboxJobId);
        handler.Release();
        Assert.Equal("newer-inbox-owner", Assert.Single((await fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
    }

    [Fact]
    public async Task Outbox_StaleGenerationCompletesWithoutClearingNewerOwner()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/stale-outbox-generation",
            NewMessage(61, "newer-outbox-owner"));
        await receiverWrite.WaitUntilEnteredAsync();
        var owned = fixture.GetSnapshot(sender);
        Assert.False(string.IsNullOrEmpty(owned.OutboxJobId));
        var completionBaseline = fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var manager = fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILocalDurableJobManager>();

        try
        {
            await manager.ScheduleJobAsync(
                new ScheduleJobRequest
                {
                    Target = sender.GetGrainId(),
                    JobName = "orleans.messaging.outbox-flush",
                    DueTime = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["orleans.messaging.ownership-id"] = "0"
                    }
                },
                TestContext.Current.CancellationToken);
            await fixture.Metrics.WaitForCountAsync(
                "orleans-durablejobs-jobs-completed",
                completionBaseline + 1);

            Assert.Equal(owned.OutboxJobId, fixture.GetSnapshot(sender).OutboxJobId);
        }
        finally
        {
            receiverWrite.Release();
        }

        Assert.Equal("newer-outbox-owner", Assert.Single((await fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
    }

    [Fact]
    public async Task Outbox_JobVisibleDuringRecovery_PollsUntilCommittedOwnerIsRestored()
    {
        const string jobName = "orleans.messaging.outbox-flush";
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/recovery-visibility",
            NewMessage(62, "recovery-visibility"));
        await receiverWrite.WaitUntilEnteredAsync();
        var owned = fixture.GetSnapshot(sender);
        var ownershipId = Assert.IsType<string>(owned.OutboxJobId);
        var recoveryRead = fixture.Storage.BlockRead(JournalId.FromGrainId(sender.GetGrainId()));
        var recovery = fixture.RevertStateAsync(sender).AsTask();
        await recoveryRead.WaitUntilEnteredAsync();
        var handlerBaseline = fixture.Metrics.GetCount("orleans-durablejobs-handler-executions-started");
        var completionBaseline = fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var orphanBaseline = fixture.Metrics.GetCount(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            jobName);
        var manager = fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILocalDurableJobManager>();

        try
        {
            await manager.ScheduleJobAsync(
                new ScheduleJobRequest
                {
                    Target = sender.GetGrainId(),
                    JobName = jobName,
                    DueTime = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["orleans.messaging.ownership-id"] = ownershipId
                    }
                },
                TestContext.Current.CancellationToken);
            await fixture.Metrics.WaitForCountAsync(
                "orleans-durablejobs-handler-executions-started",
                handlerBaseline + 1);

            Assert.Equal(
                orphanBaseline,
                fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
            Assert.Equal(completionBaseline, fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed"));
        }
        finally
        {
            recoveryRead.Release();
            await recovery;
            receiverWrite.Release();
        }

        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("recovery-visibility", Assert.Single(delivered.Effects).Value);
        Assert.Equal(1, Assert.Single(delivered.Effects).Count);
    }

    [Fact]
    public async Task InboxSchedulingFailure_RevertsAcceptanceAndRetryDoesNotStrandMessage()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(59, "inbox-schedule-retry"));
        fixture.JobManagerProbe.FailAfterNext("orleans.messaging.inbox-drain");

        await Assert.ThrowsAsync<IOException>(
            () => DeliverAsync(receiver, envelope.Value));
        Assert.Equal(0, (await receiver.GetSnapshotAsync()).InboxCount);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var completed = await fixture.WaitForEffectCountAsync(receiver, 1);

        var effect = Assert.Single(completed.Effects);
        Assert.Equal("inbox-schedule-retry", effect.Value);
        Assert.Equal(1, effect.Count);
        Assert.Equal(
            2,
            fixture.JobManagerProbe.GetAttemptCount(
                "orleans.messaging.inbox-drain",
                receiver.GetGrainId()));
        Assert.Equal(
            2,
            fixture.JobManagerProbe.GetSuccessCount(
                "orleans.messaging.inbox-drain",
                receiver.GetGrainId()));
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

        var completed = await fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.NullReferenceMessageCalls == 1
                && snapshot.NullNullableValueMessageCalls == 1);
        Assert.Equal(1, completed.NullReferenceMessageCalls);
        Assert.Equal(1, completed.NullNullableValueMessageCalls);
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
        Assert.Empty(fixture.GetSnapshot(blocked).Effects);
        barrier.Release();
        Assert.Equal("blocked", Assert.Single((await fixture.WaitForEffectCountAsync(blocked, 1)).Effects).Value);
    }

    [Fact]
    public async Task DuplicateExactRouteRegistration_ThrowsAndPreservesLookupAndDispatch()
    {
        var receiver = NewGrain();
        const string route = "exact/duplicate";

        var registration = await receiver.RegisterDuplicateExactRouteHandlersAsync(route);

        Assert.Equal(
            "A handler is already registered for exact route 'exact/duplicate'.",
            registration.ExceptionMessage);
        Assert.True(registration.LookupRetainedFirstHandler);

        using var envelope = CreateEnvelope(receiver, NewMessage(69, "first-handler"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.FirstExactRouteHandlerCalls == 1);
        Assert.Equal(1, state.FirstExactRouteHandlerCalls);
        Assert.Equal(0, state.ReplacementExactRouteHandlerCalls);
        Assert.Equal(0, state.GenericExactRouteHandlerCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RouteLookup_RejectsInvalidRouteKeys(string? route)
    {
        var result = await NewGrain().ValidateRouteLookupAsync(route);

        Assert.Equal("routeKey", result.HasHandlerParameterName);
        Assert.Equal("routeKey", result.TryGetHandlerParameterName);
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
    public async Task Deliver_RejectsEnvelopeAddressedToAnotherGrain()
    {
        var receiver = NewGrain();
        var declaredReceiver = NewGrain();
        using var envelope = CreateEnvelope(
            declaredReceiver,
            NewMessage(74, "wrong-receiver"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => DeliverAsync(receiver, envelope.Value));

        Assert.Contains(declaredReceiver.GetGrainId().ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(receiver.GetGrainId().ToString(), exception.Message, StringComparison.Ordinal);
        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(0, state.InboxCount);
        Assert.Empty(state.Effects);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(declaredReceiver, envelope.Value)).Status);
        var delivered = await fixture.WaitForEffectCountAsync(declaredReceiver, 1);
        Assert.Single(delivered.Effects);
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

    [Fact]
    public async Task OutboxDeadLetterRemoval_IsDurable()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var messageId = await sender.SendAsync(
            receiver.GetGrainId(),
            "unknown/removable-outbox-route",
            NewMessage(73, "removable-undeliverable"));
        _ = await fixture.WaitForDeadLetterCountAsync(sender, 1);

        Assert.True(await sender.RemoveOutboxDeadLetterAsync(messageId));
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
        Assert.False(await sender.RemoveOutboxDeadLetterAsync(messageId));

        await sender.RequestDeactivationAsync();
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
    }

    private IDurableMessagingTestGrain NewGrain() =>
        fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());

    private static DurableTestMessage NewMessage(int sequence, string value) =>
        new(Guid.NewGuid(), sequence, value);

    private static Task<DeliveryResult> DeliverAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope) =>
        DeliverWithCancellationAsync(receiver, envelope, TestContext.Current.CancellationToken);

    private static async Task<DeliveryResult> DeliverWithCancellationAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope,
        CancellationToken cancellationToken) =>
        await receiver.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, cancellationToken);

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

    private EnvelopeLease CreateEnvelope<T>(
        IDurableMessagingTestGrain receiver,
        T body,
        string route,
        Action<DurableEnvelopeBuilder>? configure = null)
    {
        var sessions = fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("external-test-sender", Guid.NewGuid().ToString("N"));
        var builder = new DurableEnvelopeBuilder(sessions, sender).To(receiver.GetGrainId(), route);
        configure?.Invoke(builder);
        return new EnvelopeLease(builder.WithBody<T>(body).Build());
    }

    private sealed class EnvelopeLease(DurableEnvelope value) : IDisposable
    {
        public DurableEnvelope Value { get; } = value;
        public void Dispose()
        {
        }
    }
}
