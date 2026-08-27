using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Diagnostics;

namespace Orleans.Transactions.TestKit;

internal sealed class TransactionRecoveryEventObserver : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>, IDisposable
{
    private readonly object lockObj = new();
    private readonly Func<ParticipantId, bool> candidateFilter;
    private readonly IDisposable subscription;
    private readonly long startedAt = Stopwatch.GetTimestamp();
    private readonly List<RecoveryTransition> timeline = [];
    private readonly List<Waiter> waiters = [];
    private HashSet<GrainId>? relevantGrains;
    private PhaseGate? phaseGate;
    private long nextSequence;
    private bool disposed;

    public TransactionRecoveryEventObserver(IEnumerable<GrainId> candidateGrains)
        : this(CreateCandidateFilter(candidateGrains))
    {
    }

    internal TransactionRecoveryEventObserver(Func<ParticipantId, bool> candidateFilter)
    {
        this.candidateFilter = candidateFilter;
        this.subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(this);
    }

    public long LatestRelevantSequence
    {
        get
        {
            lock (this.lockObj)
            {
                for (var i = this.timeline.Count - 1; i >= 0; i--)
                {
                    if (this.IsCurrentlyRelevant(this.timeline[i]))
                    {
                        return this.timeline[i].Sequence;
                    }
                }

                return 0;
            }
        }
    }

    public void SetRelevantGrains(IEnumerable<GrainId> grainIds)
    {
        List<(Waiter Waiter, RecoveryTransition Transition)> completed = [];
        lock (this.lockObj)
        {
            this.ThrowIfDisposed();
            this.relevantGrains = grainIds.ToHashSet();
            for (var i = this.waiters.Count - 1; i >= 0; i--)
            {
                var waiter = this.waiters[i];
                var transition = this.FindTransitionAfter(waiter.AfterSequence);
                if (transition is not null)
                {
                    this.waiters.RemoveAt(i);
                    completed.Add((waiter, transition));
                }
            }
        }

        foreach (var item in completed)
        {
            item.Waiter.Completion.TrySetResult(item.Transition);
        }
    }

    internal PhaseGate GateNextTransition(Func<RecoveryTransition, bool> predicate)
    {
        lock (this.lockObj)
        {
            this.ThrowIfDisposed();
            if (this.phaseGate is not null)
            {
                throw new InvalidOperationException("A transaction recovery phase gate is already armed.");
            }

            return this.phaseGate = new(predicate, this.ReleaseGate);
        }
    }

    public async Task<RecoveryTransition> WaitForNextTransitionAsync(
        long afterSequence,
        long deadline,
        CancellationToken cancellationToken = default)
    {
        Waiter waiter;
        lock (this.lockObj)
        {
            this.ThrowIfDisposed();
            var existing = this.FindTransitionAfter(afterSequence);
            if (existing is not null)
            {
                return existing;
            }

            waiter = new(afterSequence);
            this.waiters.Add(waiter);
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= deadline)
            {
                throw new TimeoutException();
            }

            return await waiter.Completion.Task.WaitAsync(Stopwatch.GetElapsedTime(now, deadline), cancellationToken);
        }
        catch (TimeoutException)
        {
            this.RemoveWaiter(waiter);
            throw new TimeoutException(
                $"No relevant transaction recovery transition was observed before the watchdog deadline."
                + Environment.NewLine
                + this.FormatTimeline());
        }
        catch (OperationCanceledException)
        {
            this.RemoveWaiter(waiter);
            throw;
        }
    }

    public async Task<RecoveryTransition> WaitForCommitConfirmationAsync(
        long afterSequence,
        int participantCount,
        long deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 1);

        while (true)
        {
            long observedThrough;
            lock (this.lockObj)
            {
                this.ThrowIfDisposed();
                var completed = this.FindConfirmedCommitAfter(afterSequence, participantCount);
                if (completed is not null)
                {
                    return completed;
                }

                observedThrough = this.nextSequence;
            }

            await this.WaitForNextTransitionAsync(observedThrough, deadline, cancellationToken);
        }
    }

    public async Task<RecoveryTransition> WaitForRecoveryCompletionAsync(
        Guid transactionId,
        GrainId faultingGrainId,
        long afterSequence,
        long deadline,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            long observedThrough;
            lock (this.lockObj)
            {
                this.ThrowIfDisposed();
                var completed = this.timeline.FirstOrDefault(transition =>
                    transition.Sequence > afterSequence
                    && this.IsCurrentlyRelevant(transition)
                    && transition.GrainId == faultingGrainId
                    && transition.TransactionIds.Contains(transactionId)
                    && IsRecoveryCompletion(transition));
                if (completed is not null)
                {
                    return completed;
                }

                observedThrough = this.nextSequence;
            }

            await this.WaitForNextTransitionAsync(observedThrough, deadline, cancellationToken);
        }
    }

    public IReadOnlyList<RecoveryTransition> GetTimeline()
    {
        lock (this.lockObj)
        {
            return this.timeline.Where(this.IsCurrentlyRelevant).ToArray();
        }
    }

    public string FormatTimeline()
    {
        var entries = this.GetTimeline();
        if (entries.Count == 0)
        {
            return "Transaction recovery timeline: <no relevant events>";
        }

        return "Transaction recovery timeline:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, entries.Select(FormatTransition));
    }

    public static string FormatTransition(RecoveryTransition transition)
        => $"  sequence={transition.Sequence}, observedAt={transition.ObservedAtUtc:O}, elapsed={transition.Elapsed}, "
            + $"kind={transition.Kind}, transactions={FormatTransactionIds(transition.TransactionIds)}, "
            + $"role={transition.ProtocolRole}, phase={transition.Phase}, "
            + $"resource={transition.ResourceName}, grain={transition.GrainId?.ToString() ?? "<none>"}, "
            + $"silo={transition.SiloAddress?.ToString() ?? "<none>"}, "
            + $"activation={(transition.ActivationId.IsDefault ? "<none>" : transition.ActivationId.ToString())}, "
            + $"status={transition.Status ?? "<none>"}";

    public void Dispose()
    {
        List<Waiter> waiters;
        PhaseGate? phaseGate;
        lock (this.lockObj)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            waiters = [.. this.waiters];
            this.waiters.Clear();
            phaseGate = this.phaseGate;
            this.phaseGate = null;
        }

        this.subscription.Dispose();
        phaseGate?.Release();
        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(TransactionRecoveryEventObserver)));
        }
    }

    void IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>.OnCompleted()
    {
    }

    void IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>.OnError(Exception error)
    {
    }

    void IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>.OnNext(
        TransactionDiagnosticEvents.TransactionDiagnosticEvent value)
    {
        if (!this.candidateFilter(value.Resource)
            || !TryCreateTransition(value, Stopwatch.GetElapsedTime(this.startedAt), out var transition))
        {
            return;
        }

        List<Waiter> completed = [];
        PhaseGate? reachedGate = null;
        var isRelevant = false;
        lock (this.lockObj)
        {
            if (this.disposed)
            {
                return;
            }

            transition = transition with { Sequence = ++this.nextSequence };
            this.timeline.Add(transition);
            if (this.phaseGate is { } phaseGate
                && phaseGate.Predicate(transition)
                && phaseGate.TryReach(transition))
            {
                this.phaseGate = null;
                reachedGate = phaseGate;
            }

            isRelevant = this.IsCurrentlyRelevant(transition);
            if (isRelevant)
            {
                for (var i = this.waiters.Count - 1; i >= 0; i--)
                {
                    if (transition.Sequence > this.waiters[i].AfterSequence)
                    {
                        completed.Add(this.waiters[i]);
                        this.waiters.RemoveAt(i);
                    }
                }
            }
        }

        if (isRelevant)
        {
            foreach (var waiter in completed)
            {
                waiter.Completion.TrySetResult(transition);
            }
        }

        reachedGate?.Block();
    }

    private static Func<ParticipantId, bool> CreateCandidateFilter(IEnumerable<GrainId> candidateGrains)
    {
        var candidates = candidateGrains.ToHashSet();
        return resource => resource.Reference is not null && candidates.Contains(resource.Reference.GrainId);
    }

    private static bool TryCreateTransition(
        TransactionDiagnosticEvents.TransactionDiagnosticEvent evt,
        TimeSpan elapsed,
        out RecoveryTransition transition)
    {
        var kind = evt switch
        {
            TransactionDiagnosticEvents.StorageWriteCompleted => RecoveryTransitionKind.StorageWriteCompleted,
            TransactionDiagnosticEvents.TransactionManagerWaitingForPrepared => RecoveryTransitionKind.TransactionManagerWaitingForPrepared,
            TransactionDiagnosticEvents.RemotePreparePersisted => RecoveryTransitionKind.RemotePreparePersisted,
            TransactionDiagnosticEvents.RemotePreparedSent => RecoveryTransitionKind.RemotePreparedSent,
            TransactionDiagnosticEvents.PrepareTimedOut => RecoveryTransitionKind.PrepareTimedOut,
            TransactionDiagnosticEvents.RemoteRecoveryPingSent => RecoveryTransitionKind.RemoteRecoveryPingSent,
            TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted => RecoveryTransitionKind.TransactionManagerAbortDecisionCompleted,
            TransactionDiagnosticEvents.TransactionCancelCompleted => RecoveryTransitionKind.TransactionCancelCompleted,
            TransactionDiagnosticEvents.TransactionConfirmCompleted => RecoveryTransitionKind.TransactionConfirmCompleted,
            TransactionDiagnosticEvents.CancelSendStarted => RecoveryTransitionKind.CancelSendStarted,
            TransactionDiagnosticEvents.CancelSendCompleted => RecoveryTransitionKind.CancelSendCompleted,
            TransactionDiagnosticEvents.CancelSendFailed => RecoveryTransitionKind.CancelSendFailed,
            TransactionDiagnosticEvents.CancelFanOutStarted => RecoveryTransitionKind.CancelFanOutStarted,
            TransactionDiagnosticEvents.CancelFanOutCompleted => RecoveryTransitionKind.CancelFanOutCompleted,
            TransactionDiagnosticEvents.CancelFanOutFailed => RecoveryTransitionKind.CancelFanOutFailed,
            TransactionDiagnosticEvents.ReadyWaitStarted => RecoveryTransitionKind.ReadyWaitStarted,
            TransactionDiagnosticEvents.ReadyWaitCompleted => RecoveryTransitionKind.ReadyWaitCompleted,
            TransactionDiagnosticEvents.ReadyWaitFailed => RecoveryTransitionKind.ReadyWaitFailed,
            TransactionDiagnosticEvents.DeactivationRequested => RecoveryTransitionKind.DeactivationRequested,
            TransactionDiagnosticEvents.StorageConflictDetected => RecoveryTransitionKind.StorageConflict,
            TransactionDiagnosticEvents.AbortAndRestoreCompleted => RecoveryTransitionKind.AbortAndRestoreCompleted,
            TransactionDiagnosticEvents.QueueRestoreCompleted => RecoveryTransitionKind.QueueRestoreCompleted,
            TransactionDiagnosticEvents.QueueRestoreFailed => RecoveryTransitionKind.QueueRestoreFailed,
            TransactionDiagnosticEvents.LockExpired => RecoveryTransitionKind.LockExpired,
            TransactionDiagnosticEvents.LockBroken => RecoveryTransitionKind.LockBroken,
            _ => (RecoveryTransitionKind?)null,
        };

        if (kind is null)
        {
            transition = null!;
            return false;
        }

        var transactionIds = evt switch
        {
            TransactionDiagnosticEvents.TransactionEvent transactionEvent => ImmutableArray.Create(transactionEvent.TransactionId),
            TransactionDiagnosticEvents.StorageWriteCompleted completedWrite => completedWrite.TransactionIds,
            TransactionDiagnosticEvents.LockExpired lockExpired => ImmutableArray.Create(lockExpired.TransactionId),
            TransactionDiagnosticEvents.LockBroken lockBroken => ImmutableArray.Create(lockBroken.TransactionId),
            TransactionDiagnosticEvents.StorageConflictDetected conflict => conflict.TransactionIds,
            TransactionDiagnosticEvents.AbortAndRestoreCompleted restored => restored.TransactionIds,
            TransactionDiagnosticEvents.QueueRestoreCompleted restored => restored.TransactionIds,
            TransactionDiagnosticEvents.QueueRestoreFailed failed => failed.TransactionIds,
            TransactionDiagnosticEvents.ReadyWaitEvent ready when ready.TransactionId is { } transactionId =>
                ImmutableArray.Create(transactionId),
            TransactionDiagnosticEvents.DeactivationRequested deactivation => deactivation.TransactionIds,
            _ => ImmutableArray<Guid>.Empty,
        };
        var status = evt switch
        {
            TransactionDiagnosticEvents.StorageWriteCompleted stored =>
                $"batchSize={stored.BatchSize}, commitCount={stored.CommitCount}, eTag={stored.ETag ?? "<none>"}",
            TransactionDiagnosticEvents.TransactionManagerWaitingForPrepared waiting =>
                $"remaining={waiting.WaitCount}, deadline={waiting.Deadline:O}",
            TransactionDiagnosticEvents.PrepareTimedOut timedOut => $"remaining={timedOut.RemainingCount}",
            TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted aborted => aborted.Status.ToString(),
            TransactionDiagnosticEvents.TransactionCancelCompleted canceled =>
                $"{canceled.Status}, queueEntryFound={canceled.QueueEntryFound}, succeeded={canceled.Succeeded}",
            TransactionDiagnosticEvents.TransactionConfirmCompleted confirmed =>
                $"{confirmed.Status}, queueEntryFound={confirmed.QueueEntryFound}, succeeded={confirmed.Succeeded}",
            TransactionDiagnosticEvents.CancelSendStarted cancel =>
                $"{cancel.Status}, target={cancel.Target.Name}, isSelf={cancel.IsSelf}, reason={cancel.Reason}",
            TransactionDiagnosticEvents.CancelSendCompleted cancel =>
                $"{cancel.Status}, target={cancel.Target.Name}, isSelf={cancel.IsSelf}, reason={cancel.Reason}",
            TransactionDiagnosticEvents.CancelSendFailed cancel =>
                $"{cancel.Status}, target={cancel.Target.Name}, isSelf={cancel.IsSelf}, reason={cancel.Reason}, "
                + $"exception={cancel.ExceptionType}",
            TransactionDiagnosticEvents.CancelFanOutStarted fanOut =>
                $"{fanOut.Status}, targets={fanOut.TargetCount}, selfTargets={fanOut.SelfTargetCount}",
            TransactionDiagnosticEvents.CancelFanOutCompleted fanOut =>
                $"{fanOut.Status}, targets={fanOut.TargetCount}, selfTargets={fanOut.SelfTargetCount}, "
                + $"duration={fanOut.Duration}",
            TransactionDiagnosticEvents.CancelFanOutFailed fanOut =>
                $"{fanOut.Status}, targets={fanOut.TargetCount}, selfTargets={fanOut.SelfTargetCount}, "
                + $"duration={fanOut.Duration}, exception={fanOut.ExceptionType}",
            TransactionDiagnosticEvents.ReadyWaitStarted => "started",
            TransactionDiagnosticEvents.ReadyWaitCompleted ready =>
                $"recoveredAfterFailure={ready.RecoveredAfterFailure}",
            TransactionDiagnosticEvents.ReadyWaitFailed ready => $"exception={ready.ExceptionType}",
            TransactionDiagnosticEvents.DeactivationRequested deactivation =>
                $"{deactivation.Status}, failureCount={deactivation.FailureCount}",
            TransactionDiagnosticEvents.StorageConflictDetected conflict =>
                $"operation={conflict.Operation}, storageOutcomeInDoubt={conflict.StorageOutcomeInDoubt}, "
                + $"queued={conflict.QueuedTransactionCount}, exception={conflict.ExceptionType}",
            TransactionDiagnosticEvents.AbortAndRestoreCompleted restored =>
                $"{restored.Status}, storageOutcomeInDoubt={restored.StorageOutcomeInDoubt}",
            TransactionDiagnosticEvents.QueueRestoreCompleted restored =>
                $"pending={restored.RecoveredPendingCount}, commits={restored.RecoveredCommitCount}",
            TransactionDiagnosticEvents.QueueRestoreFailed failed =>
                $"storageConflict={failed.StorageConflict}, exception={failed.ExceptionType}",
            TransactionDiagnosticEvents.LockExpired expired =>
                $"{expired.Kind}, deadline={expired.Deadline:O}, observedAt={expired.ObservedAt:O}",
            TransactionDiagnosticEvents.LockBroken broken => broken.Reason.ToString(),
            _ => null,
        };

        transition = new(
            Sequence: 0,
            ObservedAtUtc: DateTime.UtcNow,
            elapsed,
            kind.Value,
            transactionIds,
            evt.ProtocolRole,
            evt.Phase,
            evt.Resource.Name,
            evt.Resource.Reference is null ? null : evt.Resource.Reference.GrainId,
            evt.SiloAddress,
            evt.ActivationId,
            status,
            evt is TransactionDiagnosticEvents.StorageWriteCompleted storageWrite ? storageWrite.CommitCount : null,
            evt switch
            {
                TransactionDiagnosticEvents.TransactionConfirmCompleted confirmed => confirmed.Succeeded,
                TransactionDiagnosticEvents.TransactionCancelCompleted canceled => canceled.Succeeded,
                _ => null,
            });
        return true;
    }

    private static string FormatTransactionIds(ImmutableArray<Guid> transactionIds)
        => transactionIds.IsDefaultOrEmpty ? "<none>" : $"[{string.Join(",", transactionIds)}]";

    private static bool IsRecoveryCompletion(RecoveryTransition transition)
        => transition.Kind is RecoveryTransitionKind.AbortAndRestoreCompleted
            or RecoveryTransitionKind.QueueRestoreCompleted
            || ((transition.Kind is RecoveryTransitionKind.TransactionCancelCompleted
                    or RecoveryTransitionKind.TransactionConfirmCompleted)
                && transition.Succeeded == true);

    private bool IsCurrentlyRelevant(RecoveryTransition transition)
        => this.relevantGrains is null
            || transition.GrainId is { } grainId && this.relevantGrains.Contains(grainId);

    private RecoveryTransition? FindTransitionAfter(long sequence)
        => this.timeline.FirstOrDefault(transition => transition.Sequence > sequence && this.IsCurrentlyRelevant(transition));

    private RecoveryTransition? FindConfirmedCommitAfter(long sequence, int participantCount)
    {
        foreach (var commit in this.timeline)
        {
            if (commit.Sequence <= sequence
                || !this.IsCurrentlyRelevant(commit)
                || commit.Kind != RecoveryTransitionKind.StorageWriteCompleted
                || commit.CommitCount <= 0)
            {
                continue;
            }

            foreach (var transactionId in commit.TransactionIds)
            {
                var confirmedParticipants = this.timeline
                    .Where(transition =>
                        transition.Sequence > commit.Sequence
                        && this.IsCurrentlyRelevant(transition)
                        && transition.Kind == RecoveryTransitionKind.TransactionConfirmCompleted
                        && transition.TransactionId == transactionId
                        && transition.Succeeded == true
                        && transition.GrainId != commit.GrainId)
                    .Select(transition => transition.GrainId)
                    .Where(grainId => grainId is not null)
                    .Distinct()
                    .Count();
                if (confirmedParticipants >= participantCount - 1)
                {
                    return commit;
                }
            }
        }

        return null;
    }

    private void RemoveWaiter(Waiter waiter)
    {
        lock (this.lockObj)
        {
            this.waiters.Remove(waiter);
        }
    }

    private void ReleaseGate(PhaseGate gate)
    {
        lock (this.lockObj)
        {
            if (ReferenceEquals(this.phaseGate, gate))
            {
                this.phaseGate = null;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
    }

    private sealed class Waiter(long afterSequence)
    {
        public long AfterSequence { get; } = afterSequence;
        public TaskCompletionSource<RecoveryTransition> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal enum RecoveryTransitionKind
    {
        StorageWriteCompleted,
        TransactionManagerWaitingForPrepared,
        RemotePreparePersisted,
        RemotePreparedSent,
        PrepareTimedOut,
        RemoteRecoveryPingSent,
        TransactionManagerAbortDecisionCompleted,
        TransactionCancelCompleted,
        TransactionConfirmCompleted,
        CancelSendStarted,
        CancelSendCompleted,
        CancelSendFailed,
        CancelFanOutStarted,
        CancelFanOutCompleted,
        CancelFanOutFailed,
        ReadyWaitStarted,
        ReadyWaitCompleted,
        ReadyWaitFailed,
        DeactivationRequested,
        StorageConflict,
        AbortAndRestoreCompleted,
        QueueRestoreCompleted,
        QueueRestoreFailed,
        LockExpired,
        LockBroken,
    }

    internal sealed record RecoveryTransition(
        long Sequence,
        DateTime ObservedAtUtc,
        TimeSpan Elapsed,
        RecoveryTransitionKind Kind,
        ImmutableArray<Guid> TransactionIds,
        TransactionDiagnosticEvents.TransactionProtocolRole ProtocolRole,
        TransactionDiagnosticEvents.TransactionPhase Phase,
        string ResourceName,
        GrainId? GrainId,
        SiloAddress? SiloAddress,
        ActivationId ActivationId,
        string? Status,
        int? CommitCount,
        bool? Succeeded)
    {
        public Guid? TransactionId => this.TransactionIds.Length == 1 ? this.TransactionIds[0] : null;
    }

    internal sealed class PhaseGate(
        Func<RecoveryTransition, bool> predicate,
        Action<PhaseGate> onDisposed) : IDisposable
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RecoveryTransition> reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int state;

        internal Func<RecoveryTransition, bool> Predicate { get; } = predicate;

        internal bool TryReach(RecoveryTransition transition)
        {
            if (Interlocked.CompareExchange(ref this.state, 1, 0) != 0)
            {
                return false;
            }

            this.reached.TrySetResult(transition);
            return true;
        }

        internal void Block() => this.release.Task.GetAwaiter().GetResult();

        internal void Release() => this.release.TrySetResult();

        internal Task<RecoveryTransition> WaitAsync(long deadline) =>
            WaitAsync(deadline, CancellationToken.None);

        internal async Task<RecoveryTransition> WaitAsync(
            long deadline,
            CancellationToken cancellationToken)
        {
            if (this.reached.Task.IsCompleted)
            {
                return await this.reached.Task.WaitAsync(cancellationToken);
            }

            var now = Stopwatch.GetTimestamp();
            if (now >= deadline)
            {
                throw new TimeoutException("The transaction recovery phase gate was not reached before the deadline.");
            }

            try
            {
                return await this.reached.Task.WaitAsync(
                    Stopwatch.GetElapsedTime(now, deadline),
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("The transaction recovery phase gate was not reached before the deadline.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.state, 2) == 2)
            {
                return;
            }

            this.release.TrySetResult();
            onDisposed(this);
        }
    }
}
