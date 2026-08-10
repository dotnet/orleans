using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.Transactions.Diagnostics;

internal static class TransactionDiagnosticEvents
{
    internal const string ListenerName = "Orleans.Transactions";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<TransactionDiagnosticEvent> AllEvents { get; } = new Observable();

    internal readonly struct TransactionDiagnosticIdentity(SiloAddress? siloAddress, ActivationId activationId)
    {
        public readonly SiloAddress? SiloAddress = siloAddress;
        public readonly ActivationId ActivationId = activationId;
    }

    internal enum TransactionProtocolRole
    {
        Unknown,
        LocalTransactionManager,
        RemoteParticipant,
    }

    internal enum TransactionPhase
    {
        Unknown,
        StorageWrite,
        WaitingForRemotePrepares,
        PreparedCallback,
        PrepareTimeout,
        RemotePreparePersisted,
        RemotePreparedSent,
        RecoveryPingScheduled,
        RecoveryPingSent,
        QueueRestore,
        Lock,
        StorageConflict,
        AbortAndRestore,
        Deactivation,
        Cancel,
        Confirm,
        CancelFanOut,
        AbortDecision,
        ReadyWait,
    }

    internal abstract class TransactionDiagnosticEvent(ParticipantId resource)
    {
        public readonly ParticipantId Resource = resource;
        public SiloAddress? SiloAddress { get; private set; }
        public ActivationId ActivationId { get; private set; }
        public TransactionProtocolRole ProtocolRole { get; private set; }
        public TransactionPhase Phase { get; private set; }

        internal void SetContext(
            TransactionDiagnosticIdentity identity,
            TransactionProtocolRole protocolRole,
            TransactionPhase phase)
        {
            SiloAddress = identity.SiloAddress;
            ActivationId = identity.ActivationId;
            ProtocolRole = protocolRole;
            Phase = phase;
        }
    }

    internal sealed class StorageWriteCompleted(
        ParticipantId resource,
        string? eTag,
        int batchSize,
        int commitCount,
        ImmutableArray<Guid> transactionIds) : TransactionDiagnosticEvent(resource)
    {
        public readonly string? ETag = eTag;
        public readonly int BatchSize = batchSize;
        public readonly int CommitCount = commitCount;
        public readonly ImmutableArray<Guid> TransactionIds = transactionIds;
    }

    internal abstract class TransactionEvent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp) : TransactionDiagnosticEvent(resource)
    {
        public readonly Guid TransactionId = transactionId;
        public readonly DateTime TimeStamp = timeStamp;
    }

    internal sealed class TransactionManagerWaitingForPrepared(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        int waitCount,
        DateTime deadline) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly int WaitCount = waitCount;
        public readonly DateTime Deadline = deadline;
    }

    internal sealed class PreparedReceived(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId participant,
        TransactionalStatus status,
        int? remainingCount) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId Participant = participant;
        public readonly TransactionalStatus Status = status;
        public readonly int? RemainingCount = remainingCount;
    }

    internal sealed class PrepareTimedOut(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        int remainingCount,
        DateTime deadline) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly int RemainingCount = remainingCount;
        public readonly DateTime Deadline = deadline;
    }

    internal sealed class RemotePreparePersisted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId TransactionManager = transactionManager;
    }

    internal sealed class RemotePreparedSent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime sentAt) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId TransactionManager = transactionManager;
        public readonly DateTime SentAt = sentAt;
    }

    internal sealed class RemoteRecoveryPingScheduled(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime scheduledAt) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId TransactionManager = transactionManager;
        public readonly DateTime ScheduledAt = scheduledAt;
    }

    internal sealed class RemoteRecoveryPingSent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime sentAt) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId TransactionManager = transactionManager;
        public readonly DateTime SentAt = sentAt;
    }

    internal sealed class TransactionCancelCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        bool queueEntryFound,
        bool succeeded) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly TransactionalStatus Status = status;
        public readonly bool QueueEntryFound = queueEntryFound;
        public readonly bool Succeeded = succeeded;
    }

    internal sealed class TransactionConfirmCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        bool queueEntryFound,
        bool succeeded) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly TransactionalStatus Status = status;
        public readonly bool QueueEntryFound = queueEntryFound;
        public readonly bool Succeeded = succeeded;
    }

    internal sealed class TransactionManagerAbortDecisionCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly TransactionalStatus Status = status;
    }

    internal sealed class QueueRestoreStarted(
        ParticipantId resource,
        ImmutableArray<Guid> transactionIds) : TransactionDiagnosticEvent(resource)
    {
        public readonly ImmutableArray<Guid> TransactionIds = transactionIds;
    }

    internal sealed class QueueRestoreCompleted(
        ParticipantId resource,
        long committedSequence,
        int recoveredPendingCount,
        int recoveredCommitCount,
        ImmutableArray<Guid> transactionIds) : TransactionDiagnosticEvent(resource)
    {
        public readonly long CommittedSequence = committedSequence;
        public readonly int RecoveredPendingCount = recoveredPendingCount;
        public readonly int RecoveredCommitCount = recoveredCommitCount;
        public readonly ImmutableArray<Guid> TransactionIds = transactionIds;
    }

    internal sealed class QueueRestoreFailed(
        ParticipantId resource,
        string exceptionType,
        string exceptionMessage,
        bool storageConflict,
        ImmutableArray<Guid> transactionIds) : TransactionDiagnosticEvent(resource)
    {
        public readonly string ExceptionType = exceptionType;
        public readonly string ExceptionMessage = exceptionMessage;
        public readonly bool StorageConflict = storageConflict;
        public readonly ImmutableArray<Guid> TransactionIds = transactionIds;
    }

    internal sealed class LockExpired(
        ParticipantId resource,
        Guid transactionId,
        DateTime deadline,
        DateTime observedAt,
        LockExpirationKind kind) : TransactionDiagnosticEvent(resource)
    {
        public readonly Guid TransactionId = transactionId;
        public readonly DateTime Deadline = deadline;
        public readonly DateTime ObservedAt = observedAt;
        public readonly LockExpirationKind Kind = kind;
    }

    internal enum LockExpirationKind
    {
        HeldLock,
        QueuedWaiter,
    }

    internal enum LockBreakReason
    {
        Conflict,
        ValidationFailure,
        Expired,
        TransactionAbort,
        StorageRecovery,
    }

    internal sealed class LockBroken(
        ParticipantId resource,
        Guid transactionId,
        LockBreakReason reason) : TransactionDiagnosticEvent(resource)
    {
        public readonly Guid TransactionId = transactionId;
        public readonly LockBreakReason Reason = reason;
    }

    internal enum StorageOperation
    {
        Load,
        Store,
    }

    internal sealed class StorageConflictDetected(
        ParticipantId resource,
        StorageOperation operation,
        bool storageOutcomeInDoubt,
        int queuedTransactionCount,
        string exceptionType,
        string exceptionMessage,
        ImmutableArray<Guid> transactionIds) : TransactionDiagnosticEvent(resource)
    {
        public readonly StorageOperation Operation = operation;
        public readonly bool StorageOutcomeInDoubt = storageOutcomeInDoubt;
        public readonly int QueuedTransactionCount = queuedTransactionCount;
        public readonly string ExceptionType = exceptionType;
        public readonly string ExceptionMessage = exceptionMessage;
        public readonly ImmutableArray<Guid> TransactionIds = transactionIds;
    }

    internal sealed class AbortAndRestoreStarted(
        ParticipantId resource,
        TransactionalStatus status,
        bool storageOutcomeInDoubt,
        int queuedTransactionCount,
        ImmutableArray<Guid> transactionIds) : TransactionDiagnosticEvent(resource)
    {
        public readonly TransactionalStatus Status = status;
        public readonly bool StorageOutcomeInDoubt = storageOutcomeInDoubt;
        public readonly int QueuedTransactionCount = queuedTransactionCount;
        public readonly ImmutableArray<Guid> TransactionIds = transactionIds;
    }

    internal sealed class AbortAndRestoreCompleted(
        ParticipantId resource,
        TransactionalStatus status,
        bool storageOutcomeInDoubt,
        ImmutableArray<Guid> transactionIds) : TransactionDiagnosticEvent(resource)
    {
        public readonly TransactionalStatus Status = status;
        public readonly bool StorageOutcomeInDoubt = storageOutcomeInDoubt;
        public readonly ImmutableArray<Guid> TransactionIds = transactionIds;
    }

    internal sealed class DeactivationRequested(
        ParticipantId resource,
        TransactionalStatus status,
        int failureCount,
        ImmutableArray<Guid> transactionIds) : TransactionDiagnosticEvent(resource)
    {
        public readonly TransactionalStatus Status = status;
        public readonly int FailureCount = failureCount;
        public readonly ImmutableArray<Guid> TransactionIds = transactionIds;
    }

    internal enum CancelReason
    {
        TransactionAbort,
        RecoveryPing,
    }

    internal abstract class CancelSendEvent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId target,
        bool isSelf,
        TransactionalStatus status,
        CancelReason reason) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId Target = target;
        public readonly bool IsSelf = isSelf;
        public readonly TransactionalStatus Status = status;
        public readonly CancelReason Reason = reason;
    }

    internal sealed class CancelSendStarted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId target,
        bool isSelf,
        TransactionalStatus status,
        CancelReason reason) : CancelSendEvent(resource, transactionId, timeStamp, target, isSelf, status, reason);

    internal sealed class CancelSendCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId target,
        bool isSelf,
        TransactionalStatus status,
        CancelReason reason) : CancelSendEvent(resource, transactionId, timeStamp, target, isSelf, status, reason);

    internal sealed class CancelSendFailed(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId target,
        bool isSelf,
        TransactionalStatus status,
        CancelReason reason,
        string exceptionType,
        string exceptionMessage) : CancelSendEvent(resource, transactionId, timeStamp, target, isSelf, status, reason)
    {
        public readonly string ExceptionType = exceptionType;
        public readonly string ExceptionMessage = exceptionMessage;
    }

    internal abstract class CancelFanOutEvent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        int targetCount,
        int selfTargetCount) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly TransactionalStatus Status = status;
        public readonly int TargetCount = targetCount;
        public readonly int SelfTargetCount = selfTargetCount;
    }

    internal sealed class CancelFanOutStarted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        int targetCount,
        int selfTargetCount) : CancelFanOutEvent(resource, transactionId, timeStamp, status, targetCount, selfTargetCount);

    internal sealed class CancelFanOutCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        int targetCount,
        int selfTargetCount,
        TimeSpan duration) : CancelFanOutEvent(resource, transactionId, timeStamp, status, targetCount, selfTargetCount)
    {
        public readonly TimeSpan Duration = duration;
    }

    internal sealed class CancelFanOutFailed(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        int targetCount,
        int selfTargetCount,
        TimeSpan duration,
        string exceptionType,
        string exceptionMessage) : CancelFanOutEvent(resource, transactionId, timeStamp, status, targetCount, selfTargetCount)
    {
        public readonly TimeSpan Duration = duration;
        public readonly string ExceptionType = exceptionType;
        public readonly string ExceptionMessage = exceptionMessage;
    }

    internal abstract class ReadyWaitEvent(
        ParticipantId resource,
        Guid? transactionId) : TransactionDiagnosticEvent(resource)
    {
        public readonly Guid? TransactionId = transactionId;
    }

    internal sealed class ReadyWaitStarted(
        ParticipantId resource,
        Guid? transactionId) : ReadyWaitEvent(resource, transactionId);

    internal sealed class ReadyWaitCompleted(
        ParticipantId resource,
        Guid? transactionId,
        bool recoveredAfterFailure) : ReadyWaitEvent(resource, transactionId)
    {
        public readonly bool RecoveredAfterFailure = recoveredAfterFailure;
    }

    internal sealed class ReadyWaitFailed(
        ParticipantId resource,
        Guid? transactionId,
        string exceptionType,
        string exceptionMessage) : ReadyWaitEvent(resource, transactionId)
    {
        public readonly string ExceptionType = exceptionType;
        public readonly string ExceptionMessage = exceptionMessage;
    }

    internal static void EmitStorageWriteCompleted(
        ParticipantId resource,
        string? eTag,
        int batchSize,
        int commitCount,
        ImmutableArray<Guid> transactionIds,
        TransactionDiagnosticIdentity identity = default)
    {
        if (!Listener.IsEnabled(nameof(StorageWriteCompleted)))
        {
            return;
        }

        Emit(resource, eTag, batchSize, commitCount, transactionIds, identity);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            ParticipantId resource,
            string? eTag,
            int batchSize,
            int commitCount,
            ImmutableArray<Guid> transactionIds,
            TransactionDiagnosticIdentity identity)
        {
            // Observer exceptions intentionally propagate so tests can inject post-write faults.
            var evt = new StorageWriteCompleted(resource, eTag, batchSize, commitCount, transactionIds);
            evt.SetContext(identity, TransactionProtocolRole.Unknown, TransactionPhase.StorageWrite);
            Listener.Write(nameof(StorageWriteCompleted), evt);
        }
    }

    internal static void EmitTransactionManagerWaitingForPrepared(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        int waitCount,
        DateTime deadline,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(TransactionManagerWaitingForPrepared)))
        {
            Write(
                nameof(TransactionManagerWaitingForPrepared),
                new TransactionManagerWaitingForPrepared(resource, transactionId, timeStamp, waitCount, deadline),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.WaitingForRemotePrepares);
        }
    }

    internal static void EmitPreparedReceived(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId participant,
        TransactionalStatus status,
        int? remainingCount,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(PreparedReceived)))
        {
            Write(
                nameof(PreparedReceived),
                new PreparedReceived(resource, transactionId, timeStamp, participant, status, remainingCount),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.PreparedCallback);
        }
    }

    internal static void EmitPrepareTimedOut(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        int remainingCount,
        DateTime deadline,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(PrepareTimedOut)))
        {
            Write(
                nameof(PrepareTimedOut),
                new PrepareTimedOut(resource, transactionId, timeStamp, remainingCount, deadline),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.PrepareTimeout);
        }
    }

    internal static void EmitRemotePreparePersisted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(RemotePreparePersisted)))
        {
            Write(
                nameof(RemotePreparePersisted),
                new RemotePreparePersisted(resource, transactionId, timeStamp, transactionManager),
                identity,
                TransactionProtocolRole.RemoteParticipant,
                TransactionPhase.RemotePreparePersisted);
        }
    }

    internal static void EmitRemotePreparedSent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime sentAt,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(RemotePreparedSent)))
        {
            Write(
                nameof(RemotePreparedSent),
                new RemotePreparedSent(resource, transactionId, timeStamp, transactionManager, sentAt),
                identity,
                TransactionProtocolRole.RemoteParticipant,
                TransactionPhase.RemotePreparedSent);
        }
    }

    internal static void EmitRemoteRecoveryPingScheduled(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime scheduledAt,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(RemoteRecoveryPingScheduled)))
        {
            Write(
                nameof(RemoteRecoveryPingScheduled),
                new RemoteRecoveryPingScheduled(resource, transactionId, timeStamp, transactionManager, scheduledAt),
                identity,
                TransactionProtocolRole.RemoteParticipant,
                TransactionPhase.RecoveryPingScheduled);
        }
    }

    internal static void EmitRemoteRecoveryPingSent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime sentAt,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(RemoteRecoveryPingSent)))
        {
            Write(
                nameof(RemoteRecoveryPingSent),
                new RemoteRecoveryPingSent(resource, transactionId, timeStamp, transactionManager, sentAt),
                identity,
                TransactionProtocolRole.RemoteParticipant,
                TransactionPhase.RecoveryPingSent);
        }
    }

    internal static void EmitTransactionCancelCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        bool queueEntryFound,
        bool succeeded,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(TransactionCancelCompleted)))
        {
            Write(
                nameof(TransactionCancelCompleted),
                new TransactionCancelCompleted(resource, transactionId, timeStamp, status, queueEntryFound, succeeded),
                identity,
                TransactionProtocolRole.RemoteParticipant,
                TransactionPhase.Cancel);
        }
    }

    internal static void EmitTransactionConfirmCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        bool queueEntryFound,
        bool succeeded,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(TransactionConfirmCompleted)))
        {
            Write(
                nameof(TransactionConfirmCompleted),
                new TransactionConfirmCompleted(resource, transactionId, timeStamp, status, queueEntryFound, succeeded),
                identity,
                TransactionProtocolRole.RemoteParticipant,
                TransactionPhase.Confirm);
        }
    }

    internal static void EmitTransactionManagerAbortDecisionCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(TransactionManagerAbortDecisionCompleted)))
        {
            Write(
                nameof(TransactionManagerAbortDecisionCompleted),
                new TransactionManagerAbortDecisionCompleted(resource, transactionId, timeStamp, status),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.AbortDecision);
        }
    }

    internal static void EmitQueueRestoreStarted(
        ParticipantId resource,
        ImmutableArray<Guid> transactionIds,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(QueueRestoreStarted)))
        {
            Write(
                nameof(QueueRestoreStarted),
                new QueueRestoreStarted(resource, transactionIds),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.QueueRestore);
        }
    }

    internal static void EmitQueueRestoreCompleted(
        ParticipantId resource,
        long committedSequence,
        int recoveredPendingCount,
        int recoveredCommitCount,
        ImmutableArray<Guid> transactionIds,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(QueueRestoreCompleted)))
        {
            Write(
                nameof(QueueRestoreCompleted),
                new QueueRestoreCompleted(
                    resource,
                    committedSequence,
                    recoveredPendingCount,
                    recoveredCommitCount,
                    transactionIds),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.QueueRestore);
        }
    }

    internal static void EmitQueueRestoreFailed(
        ParticipantId resource,
        Exception exception,
        bool storageConflict,
        ImmutableArray<Guid> transactionIds,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(QueueRestoreFailed)))
        {
            Write(
                nameof(QueueRestoreFailed),
                new QueueRestoreFailed(
                    resource,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    storageConflict,
                    transactionIds),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.QueueRestore);
        }
    }

    internal static void EmitLockExpired(
        ParticipantId resource,
        Guid transactionId,
        DateTime deadline,
        DateTime observedAt,
        LockExpirationKind kind,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(LockExpired)))
        {
            Write(
                nameof(LockExpired),
                new LockExpired(resource, transactionId, deadline, observedAt, kind),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.Lock);
        }
    }

    internal static void EmitLockBroken(
        ParticipantId resource,
        Guid transactionId,
        LockBreakReason reason,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(LockBroken)))
        {
            Write(
                nameof(LockBroken),
                new LockBroken(resource, transactionId, reason),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.Lock);
        }
    }

    internal static void EmitStorageConflictDetected(
        ParticipantId resource,
        StorageOperation operation,
        bool storageOutcomeInDoubt,
        int queuedTransactionCount,
        Exception exception,
        ImmutableArray<Guid> transactionIds,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(StorageConflictDetected)))
        {
            Write(
                nameof(StorageConflictDetected),
                new StorageConflictDetected(
                    resource,
                    operation,
                    storageOutcomeInDoubt,
                    queuedTransactionCount,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    transactionIds),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.StorageConflict);
        }
    }

    internal static void EmitAbortAndRestoreStarted(
        ParticipantId resource,
        TransactionalStatus status,
        bool storageOutcomeInDoubt,
        int queuedTransactionCount,
        ImmutableArray<Guid> transactionIds,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(AbortAndRestoreStarted)))
        {
            Write(
                nameof(AbortAndRestoreStarted),
                new AbortAndRestoreStarted(resource, status, storageOutcomeInDoubt, queuedTransactionCount, transactionIds),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.AbortAndRestore);
        }
    }

    internal static void EmitAbortAndRestoreCompleted(
        ParticipantId resource,
        TransactionalStatus status,
        bool storageOutcomeInDoubt,
        ImmutableArray<Guid> transactionIds,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(AbortAndRestoreCompleted)))
        {
            Write(
                nameof(AbortAndRestoreCompleted),
                new AbortAndRestoreCompleted(resource, status, storageOutcomeInDoubt, transactionIds),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.AbortAndRestore);
        }
    }

    internal static void EmitDeactivationRequested(
        ParticipantId resource,
        TransactionalStatus status,
        int failureCount,
        ImmutableArray<Guid> transactionIds,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(DeactivationRequested)))
        {
            Write(
                nameof(DeactivationRequested),
                new DeactivationRequested(resource, status, failureCount, transactionIds),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.Deactivation);
        }
    }

    internal static void EmitCancelSendStarted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId target,
        bool isSelf,
        TransactionalStatus status,
        CancelReason reason,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(CancelSendStarted)))
        {
            Write(
                nameof(CancelSendStarted),
                new CancelSendStarted(resource, transactionId, timeStamp, target, isSelf, status, reason),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.Cancel);
        }
    }

    internal static void EmitCancelSendCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId target,
        bool isSelf,
        TransactionalStatus status,
        CancelReason reason,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(CancelSendCompleted)))
        {
            Write(
                nameof(CancelSendCompleted),
                new CancelSendCompleted(resource, transactionId, timeStamp, target, isSelf, status, reason),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.Cancel);
        }
    }

    internal static void EmitCancelSendFailed(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId target,
        bool isSelf,
        TransactionalStatus status,
        CancelReason reason,
        Exception exception,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(CancelSendFailed)))
        {
            Write(
                nameof(CancelSendFailed),
                new CancelSendFailed(
                    resource,
                    transactionId,
                    timeStamp,
                    target,
                    isSelf,
                    status,
                    reason,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.Cancel);
        }
    }

    internal static void EmitCancelFanOutStarted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        int targetCount,
        int selfTargetCount,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(CancelFanOutStarted)))
        {
            Write(
                nameof(CancelFanOutStarted),
                new CancelFanOutStarted(resource, transactionId, timeStamp, status, targetCount, selfTargetCount),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.CancelFanOut);
        }
    }

    internal static void EmitCancelFanOutCompleted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        int targetCount,
        int selfTargetCount,
        TimeSpan duration,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(CancelFanOutCompleted)))
        {
            Write(
                nameof(CancelFanOutCompleted),
                new CancelFanOutCompleted(
                    resource,
                    transactionId,
                    timeStamp,
                    status,
                    targetCount,
                    selfTargetCount,
                    duration),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.CancelFanOut);
        }
    }

    internal static void EmitCancelFanOutFailed(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        TransactionalStatus status,
        int targetCount,
        int selfTargetCount,
        TimeSpan duration,
        Exception exception,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(CancelFanOutFailed)))
        {
            Write(
                nameof(CancelFanOutFailed),
                new CancelFanOutFailed(
                    resource,
                    transactionId,
                    timeStamp,
                    status,
                    targetCount,
                    selfTargetCount,
                    duration,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message),
                identity,
                TransactionProtocolRole.LocalTransactionManager,
                TransactionPhase.CancelFanOut);
        }
    }

    internal static void EmitReadyWaitStarted(
        ParticipantId resource,
        Guid? transactionId,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(ReadyWaitStarted)))
        {
            Write(
                nameof(ReadyWaitStarted),
                new ReadyWaitStarted(resource, transactionId),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.ReadyWait);
        }
    }

    internal static void EmitReadyWaitCompleted(
        ParticipantId resource,
        Guid? transactionId,
        bool recoveredAfterFailure,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(ReadyWaitCompleted)))
        {
            Write(
                nameof(ReadyWaitCompleted),
                new ReadyWaitCompleted(resource, transactionId, recoveredAfterFailure),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.ReadyWait);
        }
    }

    internal static void EmitReadyWaitFailed(
        ParticipantId resource,
        Guid? transactionId,
        Exception exception,
        TransactionDiagnosticIdentity identity = default)
    {
        if (IsEnabled(nameof(ReadyWaitFailed)))
        {
            Write(
                nameof(ReadyWaitFailed),
                new ReadyWaitFailed(
                    resource,
                    transactionId,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message),
                identity,
                TransactionProtocolRole.Unknown,
                TransactionPhase.ReadyWait);
        }
    }

    internal static bool IsEnabled(string eventName)
    {
        try
        {
            return Listener.IsEnabled(eventName);
        }
        catch
        {
            // Recovery diagnostics are observational and must not affect transaction processing.
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write(
        string eventName,
        TransactionDiagnosticEvent evt,
        TransactionDiagnosticIdentity identity,
        TransactionProtocolRole protocolRole,
        TransactionPhase phase)
    {
        try
        {
            evt.SetContext(identity, protocolRole, phase);
            Listener.Write(eventName, evt);
        }
        catch (Exception)
        {
            // Recovery diagnostics are observational. StorageWriteCompleted remains the sole fault-injection event.
        }
    }

    private sealed class Observable : IObservable<TransactionDiagnosticEvent>
    {
        public IDisposable Subscribe(IObserver<TransactionDiagnosticEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<TransactionDiagnosticEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is TransactionDiagnosticEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
