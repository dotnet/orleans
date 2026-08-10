using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Transactions.Diagnostics;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionDiagnosticEventsTests
{
    private static readonly TimeSpan RecoveryObservationTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void RecoveryEventsDeliverExpectedPayloads()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var participant = CreateParticipant("participant", ParticipantId.Role.Resource);
        var manager = CreateParticipant("manager", ParticipantId.Role.Manager);
        var transactionId = Guid.NewGuid();
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var deadline = timeStamp.AddSeconds(20);
        var observedAt = deadline.AddMilliseconds(25);
        var sentAt = timeStamp.AddSeconds(1);
        var scheduledAt = sentAt.AddSeconds(60);
        var fanOutDuration = TimeSpan.FromSeconds(30);
        var transactionIds = ImmutableArray.Create(transactionId);
        var conflictException = new InconsistentStateException(
            "DynamoDB transactional state storage conflict.",
            storedEtag: "1",
            currentEtag: "2");
        var loadException = new InconsistentStateException(
            "Could not load a consistent DynamoDB transactional state snapshot.",
            storedEtag: "1",
            currentEtag: "2");
        var timeoutException = new TimeoutException("Cancel timed out.");
        var siloAddress = SiloAddress.New(IPAddress.Loopback, 11_111, 7);
        var activationId = ActivationId.NewId();
        var identity = new TransactionDiagnosticEvents.TransactionDiagnosticIdentity(siloAddress, activationId);
        var observer = new RecordingObserver();

        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);

        TransactionDiagnosticEvents.EmitTransactionManagerWaitingForPrepared(resource, transactionId, timeStamp, 2, deadline);
        TransactionDiagnosticEvents.EmitPreparedReceived(
            resource,
            transactionId,
            timeStamp,
            participant,
            TransactionalStatus.Ok,
            remainingCount: 1);
        TransactionDiagnosticEvents.EmitPrepareTimedOut(resource, transactionId, timeStamp, 1, deadline);
        TransactionDiagnosticEvents.EmitRemotePreparePersisted(resource, transactionId, timeStamp, manager);
        TransactionDiagnosticEvents.EmitRemotePreparedSent(resource, transactionId, timeStamp, manager, sentAt);
        TransactionDiagnosticEvents.EmitRemoteRecoveryPingScheduled(resource, transactionId, timeStamp, manager, scheduledAt, identity);
        TransactionDiagnosticEvents.EmitRemoteRecoveryPingSent(resource, transactionId, timeStamp, manager, sentAt, identity);
        TransactionDiagnosticEvents.EmitTransactionManagerAbortDecisionCompleted(
            resource,
            transactionId,
            timeStamp,
            TransactionalStatus.PrepareTimeout,
            identity);
        TransactionDiagnosticEvents.EmitTransactionCancelCompleted(
            resource,
            transactionId,
            timeStamp,
            TransactionalStatus.PresumedAbort,
            queueEntryFound: true,
            succeeded: true,
            identity);
        TransactionDiagnosticEvents.EmitTransactionConfirmCompleted(
            resource,
            transactionId,
            timeStamp,
            TransactionalStatus.UnknownException,
            queueEntryFound: false,
            succeeded: false,
            identity);
        TransactionDiagnosticEvents.EmitQueueRestoreStarted(resource, transactionIds, identity);
        TransactionDiagnosticEvents.EmitQueueRestoreCompleted(resource, 42, 2, 3, transactionIds);
        TransactionDiagnosticEvents.EmitQueueRestoreFailed(
            resource,
            loadException,
            storageConflict: true,
            transactionIds);
        TransactionDiagnosticEvents.EmitStorageConflictDetected(
            resource,
            TransactionDiagnosticEvents.StorageOperation.Load,
            storageOutcomeInDoubt: false,
            queuedTransactionCount: transactionIds.Length,
            exception: loadException,
            transactionIds: transactionIds);
        TransactionDiagnosticEvents.EmitLockExpired(
            resource,
            transactionId,
            deadline,
            observedAt,
            TransactionDiagnosticEvents.LockExpirationKind.HeldLock);
        TransactionDiagnosticEvents.EmitLockBroken(
            resource,
            transactionId,
            TransactionDiagnosticEvents.LockBreakReason.Expired);
        TransactionDiagnosticEvents.EmitStorageConflictDetected(
            resource,
            TransactionDiagnosticEvents.StorageOperation.Store,
            storageOutcomeInDoubt: true,
            queuedTransactionCount: 4,
            exception: conflictException,
            transactionIds: transactionIds);
        TransactionDiagnosticEvents.EmitAbortAndRestoreStarted(
            resource,
            TransactionalStatus.StorageConflict,
            storageOutcomeInDoubt: true,
            queuedTransactionCount: 4,
            transactionIds: transactionIds);
        TransactionDiagnosticEvents.EmitAbortAndRestoreCompleted(
            resource,
            TransactionalStatus.StorageConflict,
            storageOutcomeInDoubt: true,
            transactionIds: transactionIds);
        TransactionDiagnosticEvents.EmitDeactivationRequested(
            resource,
            TransactionalStatus.StorageConflict,
            failureCount: 1,
            transactionIds);
        TransactionDiagnosticEvents.EmitCancelSendStarted(
            resource,
            transactionId,
            timeStamp,
            participant,
            isSelf: true,
            TransactionalStatus.PresumedAbort,
            TransactionDiagnosticEvents.CancelReason.RecoveryPing);
        TransactionDiagnosticEvents.EmitCancelSendCompleted(
            resource,
            transactionId,
            timeStamp,
            participant,
            isSelf: false,
            TransactionalStatus.CascadingAbort,
            TransactionDiagnosticEvents.CancelReason.TransactionAbort);
        TransactionDiagnosticEvents.EmitCancelSendFailed(
            resource,
            transactionId,
            timeStamp,
            participant,
            isSelf: true,
            TransactionalStatus.PresumedAbort,
            TransactionDiagnosticEvents.CancelReason.RecoveryPing,
            timeoutException);
        TransactionDiagnosticEvents.EmitCancelFanOutStarted(
            resource,
            transactionId,
            timeStamp,
            TransactionalStatus.BrokenLock,
            targetCount: 2,
            selfTargetCount: 1);
        TransactionDiagnosticEvents.EmitCancelFanOutCompleted(
            resource,
            transactionId,
            timeStamp,
            TransactionalStatus.BrokenLock,
            targetCount: 2,
            selfTargetCount: 1,
            duration: fanOutDuration);
        TransactionDiagnosticEvents.EmitCancelFanOutFailed(
            resource,
            transactionId,
            timeStamp,
            TransactionalStatus.BrokenLock,
            targetCount: 2,
            selfTargetCount: 1,
            duration: fanOutDuration,
            exception: timeoutException);
        TransactionDiagnosticEvents.EmitReadyWaitStarted(resource, transactionId);
        TransactionDiagnosticEvents.EmitReadyWaitFailed(resource, transactionId, timeoutException);
        TransactionDiagnosticEvents.EmitReadyWaitCompleted(resource, transactionId, recoveredAfterFailure: true);
        TransactionDiagnosticEvents.EmitStorageWriteCompleted(resource, "etag", 1, 1, transactionIds, identity);

        var waiting = observer.Single<TransactionDiagnosticEvents.TransactionManagerWaitingForPrepared>(resource);
        Assert.Equal(transactionId, waiting.TransactionId);
        Assert.Equal(timeStamp, waiting.TimeStamp);
        Assert.Equal(2, waiting.WaitCount);
        Assert.Equal(deadline, waiting.Deadline);
        Assert.Equal(TransactionDiagnosticEvents.TransactionProtocolRole.LocalTransactionManager, waiting.ProtocolRole);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.WaitingForRemotePrepares, waiting.Phase);

        var prepared = observer.Single<TransactionDiagnosticEvents.PreparedReceived>(resource);
        Assert.Equal(participant, prepared.Participant);
        Assert.Equal(TransactionalStatus.Ok, prepared.Status);
        Assert.Equal(1, prepared.RemainingCount);
        Assert.Equal(TransactionDiagnosticEvents.TransactionProtocolRole.LocalTransactionManager, prepared.ProtocolRole);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.PreparedCallback, prepared.Phase);

        var timedOut = observer.Single<TransactionDiagnosticEvents.PrepareTimedOut>(resource);
        Assert.Equal(1, timedOut.RemainingCount);
        Assert.Equal(deadline, timedOut.Deadline);

        var preparePersisted = observer.Single<TransactionDiagnosticEvents.RemotePreparePersisted>(resource);
        Assert.Equal(manager, preparePersisted.TransactionManager);
        Assert.Equal(TransactionDiagnosticEvents.TransactionProtocolRole.RemoteParticipant, preparePersisted.ProtocolRole);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.RemotePreparePersisted, preparePersisted.Phase);
        Assert.Equal(sentAt, observer.Single<TransactionDiagnosticEvents.RemotePreparedSent>(resource).SentAt);
        var pingScheduled = observer.Single<TransactionDiagnosticEvents.RemoteRecoveryPingScheduled>(resource);
        Assert.Equal(scheduledAt, pingScheduled.ScheduledAt);
        Assert.Equal(activationId, pingScheduled.ActivationId);
        var pingSent = observer.Single<TransactionDiagnosticEvents.RemoteRecoveryPingSent>(resource);
        Assert.Equal(sentAt, pingSent.SentAt);
        Assert.Equal(activationId, pingSent.ActivationId);
        var abortDecision = observer.Single<TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted>(resource);
        Assert.Equal(TransactionalStatus.PrepareTimeout, abortDecision.Status);
        Assert.Equal(activationId, abortDecision.ActivationId);
        Assert.Equal(TransactionDiagnosticEvents.TransactionProtocolRole.LocalTransactionManager, abortDecision.ProtocolRole);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.AbortDecision, abortDecision.Phase);
        var canceled = observer.Single<TransactionDiagnosticEvents.TransactionCancelCompleted>(resource);
        Assert.Equal(transactionId, canceled.TransactionId);
        Assert.Equal(timeStamp, canceled.TimeStamp);
        Assert.Equal(TransactionalStatus.PresumedAbort, canceled.Status);
        Assert.True(canceled.QueueEntryFound);
        Assert.True(canceled.Succeeded);
        Assert.Equal(siloAddress, canceled.SiloAddress);
        Assert.Equal(activationId, canceled.ActivationId);
        Assert.Equal(TransactionDiagnosticEvents.TransactionProtocolRole.RemoteParticipant, canceled.ProtocolRole);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.Cancel, canceled.Phase);
        var confirmed = observer.Single<TransactionDiagnosticEvents.TransactionConfirmCompleted>(resource);
        Assert.Equal(transactionId, confirmed.TransactionId);
        Assert.Equal(timeStamp, confirmed.TimeStamp);
        Assert.Equal(TransactionalStatus.UnknownException, confirmed.Status);
        Assert.False(confirmed.QueueEntryFound);
        Assert.False(confirmed.Succeeded);
        Assert.Equal(siloAddress, confirmed.SiloAddress);
        Assert.Equal(activationId, confirmed.ActivationId);
        Assert.Equal(TransactionDiagnosticEvents.TransactionProtocolRole.RemoteParticipant, confirmed.ProtocolRole);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.Confirm, confirmed.Phase);

        var restoreStartedEvent = observer.Single<TransactionDiagnosticEvents.QueueRestoreStarted>(resource);
        Assert.Equal(transactionIds, restoreStartedEvent.TransactionIds);
        Assert.Equal(siloAddress, restoreStartedEvent.SiloAddress);
        Assert.Equal(activationId, restoreStartedEvent.ActivationId);

        var restored = observer.Single<TransactionDiagnosticEvents.QueueRestoreCompleted>(resource);
        Assert.Equal(42, restored.CommittedSequence);
        Assert.Equal(2, restored.RecoveredPendingCount);
        Assert.Equal(3, restored.RecoveredCommitCount);
        Assert.Equal(transactionIds, restored.TransactionIds);

        var restoreFailed = observer.Single<TransactionDiagnosticEvents.QueueRestoreFailed>(resource);
        Assert.True(restoreFailed.StorageConflict);
        Assert.Equal(typeof(InconsistentStateException).FullName, restoreFailed.ExceptionType);
        Assert.Equal(loadException.Message, restoreFailed.ExceptionMessage);
        Assert.Equal(transactionIds, restoreFailed.TransactionIds);

        var expired = observer.Single<TransactionDiagnosticEvents.LockExpired>(resource);
        Assert.Equal(transactionId, expired.TransactionId);
        Assert.Equal(deadline, expired.Deadline);
        Assert.Equal(observedAt, expired.ObservedAt);
        Assert.Equal(TransactionDiagnosticEvents.LockExpirationKind.HeldLock, expired.Kind);

        var broken = observer.Single<TransactionDiagnosticEvents.LockBroken>(resource);
        Assert.Equal(TransactionDiagnosticEvents.LockBreakReason.Expired, broken.Reason);

        var conflicts = observer.All<TransactionDiagnosticEvents.StorageConflictDetected>(resource);
        var storeConflict = Assert.Single(
            conflicts,
            conflict => conflict.Operation == TransactionDiagnosticEvents.StorageOperation.Store);
        Assert.True(storeConflict.StorageOutcomeInDoubt);
        Assert.Equal(4, storeConflict.QueuedTransactionCount);
        Assert.Equal(conflictException.Message, storeConflict.ExceptionMessage);
        Assert.Equal(transactionIds, storeConflict.TransactionIds);

        var loadConflict = Assert.Single(
            conflicts,
            conflict => conflict.Operation == TransactionDiagnosticEvents.StorageOperation.Load);
        Assert.False(loadConflict.StorageOutcomeInDoubt);
        Assert.Equal(transactionIds.Length, loadConflict.QueuedTransactionCount);
        Assert.Equal(loadException.Message, loadConflict.ExceptionMessage);
        Assert.Equal(transactionIds, loadConflict.TransactionIds);

        var restoreStarted = observer.Single<TransactionDiagnosticEvents.AbortAndRestoreStarted>(resource);
        Assert.Equal(TransactionalStatus.StorageConflict, restoreStarted.Status);
        Assert.True(restoreStarted.StorageOutcomeInDoubt);
        Assert.Equal(4, restoreStarted.QueuedTransactionCount);
        Assert.Equal(transactionIds, restoreStarted.TransactionIds);

        var restoreCompleted = observer.Single<TransactionDiagnosticEvents.AbortAndRestoreCompleted>(resource);
        Assert.Equal(TransactionalStatus.StorageConflict, restoreCompleted.Status);
        Assert.True(restoreCompleted.StorageOutcomeInDoubt);
        Assert.Equal(transactionIds, restoreCompleted.TransactionIds);

        var deactivation = observer.Single<TransactionDiagnosticEvents.DeactivationRequested>(resource);
        Assert.Equal(TransactionalStatus.StorageConflict, deactivation.Status);
        Assert.Equal(1, deactivation.FailureCount);
        Assert.Equal(transactionIds, deactivation.TransactionIds);

        var cancelStarted = observer.Single<TransactionDiagnosticEvents.CancelSendStarted>(resource);
        Assert.Equal(transactionId, cancelStarted.TransactionId);
        Assert.Equal(participant, cancelStarted.Target);
        Assert.True(cancelStarted.IsSelf);
        Assert.Equal(TransactionDiagnosticEvents.CancelReason.RecoveryPing, cancelStarted.Reason);
        Assert.Equal(TransactionDiagnosticEvents.TransactionProtocolRole.LocalTransactionManager, cancelStarted.ProtocolRole);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.Cancel, cancelStarted.Phase);

        var cancelCompleted = observer.Single<TransactionDiagnosticEvents.CancelSendCompleted>(resource);
        Assert.False(cancelCompleted.IsSelf);
        Assert.Equal(TransactionalStatus.CascadingAbort, cancelCompleted.Status);
        Assert.Equal(TransactionDiagnosticEvents.CancelReason.TransactionAbort, cancelCompleted.Reason);

        var cancelFailed = observer.Single<TransactionDiagnosticEvents.CancelSendFailed>(resource);
        Assert.True(cancelFailed.IsSelf);
        Assert.Equal(typeof(TimeoutException).FullName, cancelFailed.ExceptionType);
        Assert.Equal(timeoutException.Message, cancelFailed.ExceptionMessage);

        var fanOutStarted = observer.Single<TransactionDiagnosticEvents.CancelFanOutStarted>(resource);
        Assert.Equal(2, fanOutStarted.TargetCount);
        Assert.Equal(1, fanOutStarted.SelfTargetCount);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.CancelFanOut, fanOutStarted.Phase);

        var fanOutCompleted = observer.Single<TransactionDiagnosticEvents.CancelFanOutCompleted>(resource);
        Assert.Equal(fanOutDuration, fanOutCompleted.Duration);
        Assert.Equal(
            TransactionDiagnosticEvents.TransactionProtocolRole.LocalTransactionManager,
            fanOutCompleted.ProtocolRole);

        var fanOutFailed = observer.Single<TransactionDiagnosticEvents.CancelFanOutFailed>(resource);
        Assert.Equal(fanOutDuration, fanOutFailed.Duration);
        Assert.Equal(timeoutException.Message, fanOutFailed.ExceptionMessage);

        Assert.Equal(
            transactionId,
            observer.Single<TransactionDiagnosticEvents.ReadyWaitStarted>(resource).TransactionId);
        var readyFailed = observer.Single<TransactionDiagnosticEvents.ReadyWaitFailed>(resource);
        Assert.Equal(transactionId, readyFailed.TransactionId);
        Assert.Equal(timeoutException.Message, readyFailed.ExceptionMessage);
        var readyCompleted = observer.Single<TransactionDiagnosticEvents.ReadyWaitCompleted>(resource);
        Assert.Equal(transactionId, readyCompleted.TransactionId);
        Assert.True(readyCompleted.RecoveredAfterFailure);

        var storageWrite = observer.Single<TransactionDiagnosticEvents.StorageWriteCompleted>(resource);
        Assert.Equal(siloAddress, storageWrite.SiloAddress);
        Assert.Equal(activationId, storageWrite.ActivationId);
        Assert.Equal(TransactionDiagnosticEvents.TransactionProtocolRole.Unknown, storageWrite.ProtocolRole);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.StorageWrite, storageWrite.Phase);
        Assert.Equal(transactionIds, storageWrite.TransactionIds);
    }

    [Fact]
    public void OnlyStorageWriteCompletedPropagatesObserverExceptions()
    {
        var resource = CreateParticipant("fault-target", ParticipantId.Role.Resource);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(new ThrowingObserver());

        TransactionDiagnosticEvents.EmitQueueRestoreStarted(resource, ImmutableArray<Guid>.Empty);
        TransactionDiagnosticEvents.EmitTransactionCancelCompleted(
            resource,
            Guid.NewGuid(),
            DateTime.UtcNow,
            TransactionalStatus.PresumedAbort,
            queueEntryFound: false,
            succeeded: true);

        Assert.Throws<InvalidOperationException>(
            () => TransactionDiagnosticEvents.EmitStorageWriteCompleted(
                resource,
                "etag",
                1,
                1,
                ImmutableArray<Guid>.Empty));
    }

    [Fact]
    public void RecoveryEventFilterExceptionsDoNotPropagate()
    {
        var resource = CreateParticipant("filter-fault-target", ParticipantId.Role.Resource);
        var listener = Assert.IsType<DiagnosticListener>(
            typeof(TransactionDiagnosticEvents)
                .GetField("Listener", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null));
        using var subscription = listener.Subscribe(
            new RawDiagnosticObserver(),
            static (_, _, _) => throw new InvalidOperationException("Filter fault"));

        TransactionDiagnosticEvents.EmitQueueRestoreStarted(resource, ImmutableArray<Guid>.Empty);
        Assert.Throws<InvalidOperationException>(
            () => TransactionDiagnosticEvents.EmitStorageWriteCompleted(
                resource,
                "etag",
                1,
                1,
                ImmutableArray<Guid>.Empty));
    }

    [Fact]
    public async Task RecoveryObserverStorageWriteTransitionContainsCommittedTransactionIds()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var committedTransactionIds = ImmutableArray.Create(Guid.NewGuid(), Guid.NewGuid());
        using var observer = new TransactionRecoveryEventObserver(candidate => candidate.Name == resource.Name);

        TransactionDiagnosticEvents.EmitStorageWriteCompleted(
            resource,
            "etag",
            batchSize: 3,
            commitCount: committedTransactionIds.Length,
            committedTransactionIds);

        var transition = await observer.WaitForNextTransitionAsync(
            afterSequence: 0,
            GetDeadline(RecoveryObservationTimeout));

        Assert.Equal(TransactionRecoveryEventObserver.RecoveryTransitionKind.StorageWriteCompleted, transition.Kind);
        Assert.Equal(committedTransactionIds, transition.TransactionIds);
        Assert.Equal(committedTransactionIds.Length, transition.CommitCount);
    }

    [Fact]
    public async Task RecoveryObserverLockExpiredTransitionContainsTransactionId()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var transactionId = Guid.NewGuid();
        var deadline = DateTime.UtcNow;
        using var observer = new TransactionRecoveryEventObserver(candidate => candidate.Name == resource.Name);

        TransactionDiagnosticEvents.EmitLockExpired(
            resource,
            transactionId,
            deadline,
            deadline.AddMilliseconds(1),
            TransactionDiagnosticEvents.LockExpirationKind.HeldLock);

        var transition = await observer.WaitForNextTransitionAsync(
            afterSequence: 0,
            GetDeadline(RecoveryObservationTimeout));

        Assert.Equal(TransactionRecoveryEventObserver.RecoveryTransitionKind.LockExpired, transition.Kind);
        Assert.Equal(transactionId, transition.TransactionId);
    }

    [Fact]
    public void StorageWriteCompletedFaultScopeOnlyInjectsForMatchingCommittedTransactions()
    {
        var target = CreateGrainReference("fault-target");
        var otherTarget = CreateGrainReference("other-target");
        var matchingResource = CreateParticipant("balance", target, ParticipantId.Role.Resource);
        var wrongState = CreateParticipant("other-state", target, ParticipantId.Role.Resource);
        var wrongTarget = CreateParticipant("balance", otherTarget, ParticipantId.Role.Resource);
        var transactionId = Guid.NewGuid();
        var transactionIds = ImmutableArray.Create(transactionId);
        using var fault = BankTransferDiagnosticFaults.ThrowOnStorageWriteCompleted(target);

        TransactionDiagnosticEvents.EmitStorageWriteCompleted(
            matchingResource,
            "etag",
            batchSize: 1,
            commitCount: 0,
            transactionIds);
        TransactionDiagnosticEvents.EmitStorageWriteCompleted(
            matchingResource,
            "etag",
            batchSize: 1,
            commitCount: 1,
            ImmutableArray<Guid>.Empty);
        TransactionDiagnosticEvents.EmitStorageWriteCompleted(
            wrongState,
            "etag",
            batchSize: 1,
            commitCount: 1,
            transactionIds);
        TransactionDiagnosticEvents.EmitStorageWriteCompleted(
            wrongTarget,
            "etag",
            batchSize: 1,
            commitCount: 1,
            transactionIds);

        Assert.Equal(0, fault.ObservedCount);
        Assert.False(fault.FaultInjected);

        var exception = Assert.Throws<InvalidOperationException>(
            () => TransactionDiagnosticEvents.EmitStorageWriteCompleted(
                matchingResource,
                "etag",
                batchSize: 1,
                commitCount: 1,
                transactionIds));

        Assert.Contains(transactionId.ToString(), exception.Message);
        Assert.Equal(1, fault.ObservedCount);
        Assert.True(fault.FaultInjected);
    }

    [Fact]
    public async Task RecoveryObserverFiltersEventsAndReturnsAlreadyObservedTransition()
    {
        var relevant = CreateParticipant("relevant", ParticipantId.Role.Resource);
        var unrelated = CreateParticipant("unrelated", ParticipantId.Role.Resource);
        var manager = CreateParticipant("manager", ParticipantId.Role.Manager);
        var transactionId = Guid.NewGuid();
        using var observer = new TransactionRecoveryEventObserver(resource => resource.Name == relevant.Name);

        TransactionDiagnosticEvents.EmitRemotePreparePersisted(
            unrelated,
            Guid.NewGuid(),
            DateTime.UtcNow,
            manager);
        TransactionDiagnosticEvents.EmitRemotePreparePersisted(
            relevant,
            transactionId,
            DateTime.UtcNow,
            manager);

        var transition = await observer.WaitForNextTransitionAsync(0, GetDeadline(RecoveryObservationTimeout));

        Assert.Equal(TransactionRecoveryEventObserver.RecoveryTransitionKind.RemotePreparePersisted, transition.Kind);
        Assert.Equal(transactionId, transition.TransactionId);
        Assert.Equal(relevant.Name, transition.ResourceName);
        Assert.Single(observer.GetTimeline());
    }

    [Fact]
    public async Task RecoveryObserverDoesNotMissEventBetweenStateCheckAndWait()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var manager = CreateParticipant("manager", ParticipantId.Role.Manager);
        using var observer = new TransactionRecoveryEventObserver(candidate => candidate.Name == resource.Name);
        var afterSequence = observer.LatestRelevantSequence;

        TransactionDiagnosticEvents.EmitRemotePreparedSent(
            resource,
            Guid.NewGuid(),
            DateTime.UtcNow,
            manager,
            DateTime.UtcNow);

        var transition = await observer.WaitForNextTransitionAsync(
            afterSequence,
            GetDeadline(RecoveryObservationTimeout));

        Assert.Equal(TransactionRecoveryEventObserver.RecoveryTransitionKind.RemotePreparedSent, transition.Kind);
    }

    [Fact]
    public async Task RecoveryObserverPhaseGateBlocksAtTransitionUntilReleased()
    {
        var resource = CreateParticipant("manager", ParticipantId.Role.Manager);
        var transactionId = Guid.NewGuid();
        var timeStamp = DateTime.UtcNow;
        var siloAddress = SiloAddress.New(IPAddress.Loopback, 22_223, 10);
        var activationId = ActivationId.NewId();
        var identity = new TransactionDiagnosticEvents.TransactionDiagnosticIdentity(siloAddress, activationId);
        using var observer = new TransactionRecoveryEventObserver(candidate => candidate.Name == resource.Name);
        using var gate = observer.GateNextTransition(transition =>
            transition.Kind == TransactionRecoveryEventObserver.RecoveryTransitionKind.TransactionManagerWaitingForPrepared);

        var emission = RunBlockingEmission(() => TransactionDiagnosticEvents.EmitTransactionManagerWaitingForPrepared(
            resource,
            transactionId,
            timeStamp,
            waitCount: 2,
            deadline: timeStamp.AddSeconds(10),
            identity));
        var transition = await gate.WaitAsync(GetDeadline(RecoveryObservationTimeout));

        Assert.False(emission.IsCompleted);
        Assert.Equal(transactionId, transition.TransactionId);
        Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.WaitingForRemotePrepares, transition.Phase);
        Assert.Equal(siloAddress, transition.SiloAddress);
        Assert.Equal(activationId, transition.ActivationId);

        gate.Release();
        await emission;
    }

    [Fact]
    public async Task RecoveryObserverCleanupGateIgnoresUnrelatedConfirmation()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var committedTransactionId = Guid.NewGuid();
        var unrelatedTransactionId = Guid.NewGuid();
        var timeStamp = DateTime.UtcNow;
        using var observer = new TransactionRecoveryEventObserver(candidate => candidate.Name == resource.Name);
        using var gate = observer.GateNextTransition(transition =>
            transition.Kind == TransactionRecoveryEventObserver.RecoveryTransitionKind.TransactionConfirmCompleted
            && transition.TransactionId == committedTransactionId);
        var wait = gate.WaitAsync(GetDeadline(RecoveryObservationTimeout));

        TransactionDiagnosticEvents.EmitTransactionConfirmCompleted(
            resource,
            unrelatedTransactionId,
            timeStamp,
            TransactionalStatus.Ok,
            queueEntryFound: true,
            succeeded: true);

        Assert.False(wait.IsCompleted);

        var matchingEmission = RunBlockingEmission(() => TransactionDiagnosticEvents.EmitTransactionConfirmCompleted(
            resource,
            committedTransactionId,
            timeStamp,
            TransactionalStatus.Ok,
            queueEntryFound: true,
            succeeded: true));
        var transition = await wait;

        Assert.Equal(committedTransactionId, transition.TransactionId);
        Assert.False(matchingEmission.IsCompleted);

        gate.Release();
        await matchingEmission;
    }

    [Fact]
    public async Task RecoveryObserverHonorsCancellationAndDeadline()
    {
        using var observer = new TransactionRecoveryEventObserver(_ => true);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => observer.WaitForNextTransitionAsync(
                observer.LatestRelevantSequence,
                GetDeadline(RecoveryObservationTimeout),
                canceled.Token));

        var timeout = await Assert.ThrowsAsync<TimeoutException>(
            () => observer.WaitForNextTransitionAsync(
                observer.LatestRelevantSequence,
                GetDeadline(TimeSpan.FromMilliseconds(20))));
        Assert.Contains("Transaction recovery timeline: <no relevant events>", timeout.Message);
    }

    [Fact]
    public void RecoveryObserverTimelineIsMonotonicAndDiagnostic()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var transactionId = Guid.NewGuid();
        var cohortTransactionId = Guid.NewGuid();
        var transactionIds = ImmutableArray.Create(transactionId, cohortTransactionId);
        var conflict = new InconsistentStateException("Load conflict", storedEtag: "1", currentEtag: "2");
        var siloAddress = SiloAddress.New(IPAddress.Loopback, 22_222, 9);
        var activationId = ActivationId.NewId();
        var identity = new TransactionDiagnosticEvents.TransactionDiagnosticIdentity(siloAddress, activationId);
        using var observer = new TransactionRecoveryEventObserver(candidate => candidate.Name == resource.Name);

        TransactionDiagnosticEvents.EmitPrepareTimedOut(
            resource,
            transactionId,
            DateTime.UtcNow,
            remainingCount: 2,
            DateTime.UtcNow,
            identity);
        TransactionDiagnosticEvents.EmitTransactionManagerAbortDecisionCompleted(
            resource,
            transactionId,
            DateTime.UtcNow,
            TransactionalStatus.PrepareTimeout,
            identity);
        TransactionDiagnosticEvents.EmitStorageConflictDetected(
            resource,
            TransactionDiagnosticEvents.StorageOperation.Load,
            storageOutcomeInDoubt: false,
            queuedTransactionCount: transactionIds.Length,
            conflict,
            transactionIds,
            identity);
        TransactionDiagnosticEvents.EmitQueueRestoreFailed(
            resource,
            conflict,
            storageConflict: true,
            transactionIds,
            identity);
        TransactionDiagnosticEvents.EmitTransactionCancelCompleted(
            resource,
            transactionId,
            DateTime.UtcNow,
            TransactionalStatus.PresumedAbort,
            queueEntryFound: true,
            succeeded: true,
            identity);
        TransactionDiagnosticEvents.EmitTransactionConfirmCompleted(
            resource,
            transactionId,
            DateTime.UtcNow,
            TransactionalStatus.Ok,
            queueEntryFound: false,
            succeeded: true,
            identity);
        TransactionDiagnosticEvents.EmitLockBroken(
            resource,
            transactionId,
            TransactionDiagnosticEvents.LockBreakReason.Expired,
            identity);

        var timeline = observer.GetTimeline();
        Assert.Collection(
            timeline,
            first => Assert.Equal(1, first.Sequence),
            second => Assert.Equal(2, second.Sequence),
            third => Assert.Equal(3, third.Sequence),
            fourth => Assert.Equal(4, fourth.Sequence),
            fifth => Assert.Equal(5, fifth.Sequence),
            sixth => Assert.Equal(6, sixth.Sequence),
            seventh => Assert.Equal(7, seventh.Sequence));
        var diagnostics = observer.FormatTimeline();
        Assert.Contains(transactionId.ToString(), diagnostics);
        Assert.Contains(cohortTransactionId.ToString(), diagnostics);
        Assert.Contains("resource=resource", diagnostics);
        Assert.Contains("status=remaining=2", diagnostics);
        Assert.Contains("kind=TransactionManagerAbortDecisionCompleted", diagnostics);
        Assert.Contains("status=PrepareTimeout", diagnostics);
        Assert.Contains("operation=Load", diagnostics);
        Assert.Contains("kind=QueueRestoreFailed", diagnostics);
        Assert.Contains("kind=TransactionCancelCompleted", diagnostics);
        Assert.Contains("PresumedAbort, queueEntryFound=True, succeeded=True", diagnostics);
        Assert.Contains("role=RemoteParticipant, phase=Cancel", diagnostics);
        Assert.Contains("kind=TransactionConfirmCompleted", diagnostics);
        Assert.Contains("Ok, queueEntryFound=False, succeeded=True", diagnostics);
        Assert.Contains("role=RemoteParticipant, phase=Confirm", diagnostics);
        Assert.Contains("status=Expired", diagnostics);
        Assert.Contains($"silo={siloAddress}", diagnostics);
        Assert.Contains($"activation={activationId}", diagnostics);
    }

    private static ParticipantId CreateParticipant(string name, ParticipantId.Role role)
        => CreateParticipant(name, reference: null!, role);

    private static ParticipantId CreateParticipant(
        string name,
        GrainReference reference,
        ParticipantId.Role role) => new(name, reference, role);

    private static GrainReference CreateGrainReference(string key)
        => new TestGrainReference(GrainId.Create("transaction-diagnostics-test", key));

    private static long GetDeadline(TimeSpan timeout)
        => Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

    private static Task RunBlockingEmission(Action emission)
        => Task.Factory.StartNew(
            emission,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private sealed class RecordingObserver : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>
    {
        private readonly ConcurrentQueue<TransactionDiagnosticEvents.TransactionDiagnosticEvent> events = new();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(TransactionDiagnosticEvents.TransactionDiagnosticEvent value) => events.Enqueue(value);

        public T Single<T>(ParticipantId resource)
            where T : TransactionDiagnosticEvents.TransactionDiagnosticEvent
            => Assert.Single(events.OfType<T>(), evt => evt.Resource.Name == resource.Name);

        public IEnumerable<T> All<T>(ParticipantId resource)
            where T : TransactionDiagnosticEvents.TransactionDiagnosticEvent
            => events.OfType<T>().Where(evt => evt.Resource.Name == resource.Name);
    }

    private sealed class ThrowingObserver : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(TransactionDiagnosticEvents.TransactionDiagnosticEvent value)
            => throw new InvalidOperationException("Observer fault");
    }

    private sealed class RawDiagnosticObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
        }
    }

    private sealed class TestGrainReference(GrainId grainId)
        : GrainReference(
            new GrainReferenceShared(
                grainId.Type,
                default,
                interfaceVersion: 0,
                runtime: null!,
                invokeMethodOptions: default,
                codecProvider: null!,
                copyContextPool: null!,
                serviceProvider: null!),
            grainId.Key);
}
