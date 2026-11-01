using System.Diagnostics;

namespace Orleans.Journaling;

/// <summary>Receives the entries which a journal buffer writer redirects.</summary>
internal interface IJournalEntryRedirect
{
    /// <summary>Gets the writer which receives the next entry of <paramref name="streamId"/>.</summary>
    JournalBufferWriter GetEntryWriter(JournalStreamId streamId);
}

/// <summary>
/// A local transaction over the journaled state of one manager: its writes change live state and journal into a private
/// segment, which its commit appends to the journal in one step.
/// </summary>
/// <remarks>
/// <para>
/// The segment joins the journal buffer whole, and the write which captures it reaches storage in one append, which storage
/// applies atomically. Recovery therefore finds all of a committed transaction's entries or none of them. A durable
/// transaction's effect record is one of its entries.
/// </para>
/// <para>
/// The transaction is the context of the code which runs in its flow. A write holds its state: the state's pending changes
/// from before the transaction are staged into the journal first, so the journal reproduces the state as it was, and the
/// write's entries go into the segment. Every state whose entries the segment receives is held, including states which
/// a routed write changes on its behalf, such as the inbox which accepts a message that the grain sends to itself. Reads run
/// against live state. A change which reaches the journal outside the segment from the transaction's flow throws, because
/// the transaction's abort couldn't undo it.
/// </para>
/// <para>
/// Other code sees a guard over its own context while the transaction is open. It reads the transaction's uncommitted
/// changes, as an interleaving request sees any request's intermediate state, and its writes to held states throw
/// <see cref="JournaledStateConflictException"/>. Through a context which records accesses, such as a durable task body's,
/// reads of held states throw too, because a record of an uncommitted value would outlive the transaction's abort. The guard
/// checks within the access which the context runs, so a context which returns a recorded outcome instead of running the
/// access, as a replay does, never meets the guard, and a context which records the access's outcome sees the conflict
/// thrown from the access. The host's own work, such as the outbox's deliveries or a runtime's bookkeeping, writes held
/// states directly into the journal, outside the segment: its changes commute with the transaction's, and an abort's rebuild
/// replays them.
/// </para>
/// <para>
/// The manager leaves held states out of journal captures and write acknowledgements until the transaction ends, so
/// nothing which depends on a write's durability, such as an outbox delivery or a completion source's task, observes
/// the transaction's changes before its commit is durable. An abort discards the segment and rebuilds the held states from
/// the journal, so it leaves no trace.
/// </para>
/// <para>Every member runs on the activation's scheduler, as every access to the manager's states does.</para>
/// </remarks>
internal sealed class JournalTransaction : JournaledStateContext, IJournalEntryRedirect
{
    private static readonly AsyncLocal<JournalTransaction?> CurrentTransaction = new();

    private readonly JournaledStateManager _manager;

    // The states whose entries the segment receives. A transaction writes few states, so linear searches beat hashing.
    private readonly List<HeldState> _held = [];
    private JournalBufferWriter? _segment;
    private Guard? _directGuard;
    private Exception? _doom;
    private bool _ended;

    // Whether the transaction stages a state's pending changes from before it into the journal, outside the segment.
    private bool _stagingToJournal;

    // The snapshots which the journal turned into appends while the transaction was open.
    private int _deferredSnapshots;
#if DEBUG
    private readonly List<uint> _segmentStreams = [];
#endif

    public JournalTransaction(JournaledStateManager manager) => _manager = manager;

    /// <summary>Gets or sets the transaction in whose flow the calling code runs, or <see langword="null"/>.</summary>
    internal static JournalTransaction? Current
    {
        get => CurrentTransaction.Value;
        set => CurrentTransaction.Value = value;
    }

    /// <summary>Gets the manager whose states the transaction covers.</summary>
    public JournaledStateManager Manager => _manager;

    /// <summary>Gets the states whose entries the segment receives, with their stream ids.</summary>
    public IReadOnlyList<HeldState> HeldStates => _held;

    /// <summary>Gets the exception which dooms the transaction to abort when it ends, or <see langword="null"/>.</summary>
    public Exception? DoomedBy => _doom;

    /// <summary>Gets whether the transaction holds <paramref name="state"/>.</summary>
    public bool Holds(IStateMachine state) => IndexOf(state) >= 0;

    /// <summary>
    /// Marks the transaction to abort when it ends, because <paramref name="exception"/> escaped a nested transaction or the
    /// journal can't wait for it any longer. Its later writes throw.
    /// </summary>
    public void Doom(Exception exception) => _doom ??= exception;

    /// <summary>
    /// Records that the journal turned a snapshot into an append because the transaction is open, and dooms the transaction
    /// once it has deferred <see cref="JournalPositionTracker.MaxDeferredSnapshots"/> of them.
    /// </summary>
    /// <remarks>
    /// Storage requests compaction when its journal grows too long for it, and every write appends to the journal until the
    /// transaction ends, so a transaction which stays open can't keep storage from compacting without bound.
    /// </remarks>
    public void DeferSnapshot()
    {
        if (++_deferredSnapshots > JournalPositionTracker.MaxDeferredSnapshots)
        {
            Doom(new InvalidOperationException(
                $"The journal deferred {JournalPositionTracker.MaxDeferredSnapshots} snapshots while the transaction was open, so the transaction aborts and the journal can compact."));
        }
    }

    /// <summary>Gets the context in which the calling code accesses the manager's states while this transaction is open.</summary>
    public JournaledStateContext GetContext(JournaledStateContextProvider? provider)
    {
        if (ReferenceEquals(CurrentTransaction.Value, this))
        {
            return this;
        }

        return provider?.Current is { } inner ? new Guard(this, inner) : _directGuard ??= new(this, inner: null);
    }

    protected override TResult Access<TArgument, TState, TResult>(
        in JournaledStateAccess access,
        TArgument argument,
        TState state,
        Func<TState, TResult> operation,
        Action<TState, TResult>? apply)
    {
        if (access.Kind is JournaledStateAccessKind.Read)
        {
            return Run(state, operation, apply);
        }

        ThrowIfEnded();
        if (_doom is { } doom)
        {
            throw new InvalidOperationException("The transaction is doomed to abort, so its code can't change journaled state.", doom);
        }

        Hold(access.Target);
        using var redirect = new RedirectScope(_manager.JournalWriter, this);
        return Run(state, operation, apply);
    }

    // Runs an access against the state directly.
    private static TResult Run<TState, TResult>(TState state, Func<TState, TResult> operation, Action<TState, TResult>? apply)
    {
        var result = operation(state);
        apply?.Invoke(state, result);
        return result;
    }

    JournalBufferWriter IJournalEntryRedirect.GetEntryWriter(JournalStreamId streamId)
    {
        Debug.Assert(!_ended, "An ended transaction doesn't redirect entries.");

        // An operation can journal entries of states besides its target, such as the inbox which accepts a message that the
        // grain sends to itself. Holding them keeps them out of captures until the commit, and rebuilds them on abort.
        HoldStream(streamId.Value);
#if DEBUG
        _segmentStreams.Add(streamId.Value);
#endif
        return _segment ??= _manager.CreateTransactionWriter();
    }

    /// <summary>
    /// Called for an entry of <paramref name="streamId"/> which the manager's journal writer writes outside the segment while
    /// the transaction is open.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry comes from the transaction's own flow.</exception>
    internal void OnUnredirectedEntry(JournalStreamId streamId)
    {
        if (!_stagingToJournal && ReferenceEquals(CurrentTransaction.Value, this))
        {
            throw new InvalidOperationException(
                $"Journal stream {streamId.Value} belongs to state which doesn't take part in transactions, such as IPersistentState, so a transaction's code can't change it: the change would survive the transaction's abort.");
        }
    }

    /// <summary>
    /// Commits the transaction in one synchronous block: runs <paramref name="onCommit"/> in the transaction, stages the held
    /// states' pending values into the segment, and appends the segment to the manager's journal, so the next write which
    /// storage appends atomically holds all of the transaction's entries or none of them.
    /// </summary>
    /// <returns>Whether the commit appended entries to the journal.</returns>
    /// <remarks>The caller ends the transaction afterwards; a failure leaves the segment unappended, for an abort.</remarks>
    public bool Commit(Action? onCommit)
    {
        ThrowIfEnded();
        var writer = _manager.JournalWriter;
        using (new RedirectScope(writer, this))
        {
            // The step's entries join the segment, including those of host states which don't route their writes, such as a
            // durable task's records.
            onCommit?.Invoke();
            foreach (var held in _held)
            {
                if (held.State is ICoalescingJournaledState coalescing)
                {
                    coalescing.StagePendingChanges(writer.CreateJournalStreamWriter(new(held.StreamId)));
                }
            }
        }

        if (_segment is not { CommittedEntryCount: > 0 } segment)
        {
            return false;
        }

        Debug.Assert(!segment.HasActiveEntry, "The segment holds no active entry at commit.");
#if DEBUG
        foreach (var streamId in _segmentStreams)
        {
            Debug.Assert(_held.Exists(held => held.StreamId == streamId), $"Stream {streamId} has entries in the segment, so the transaction holds its state.");
        }
#endif
        writer.AppendCommitted(segment);
        return true;
    }

    /// <summary>Ends the transaction: releases its segment, after which its writes throw.</summary>
    /// <remarks>Idempotent: an abort ends the transaction before it rebuilds the held states, so their rebuild can't race the transaction's code.</remarks>
    public void End()
    {
        _ended = true;
        if (_segment is { } segment)
        {
            // A transaction's segment is short-lived; disposing it returns its pages, so no activation retains one.
            _segment = null;
            segment.Dispose();
        }
    }

    // Holds the target's state for the transaction.
    private void Hold(JournaledStateAccessTarget target)
    {
        if (IsHolding(target))
        {
            return;
        }

        var (state, streamId) = _manager.GetTransactionalState(target);
        var index = IndexOf(state);
        if (index >= 0)
        {
            // An operation of another state journaled the state's entries before the transaction wrote it directly.
            _held[index] = _held[index] with { Target = target };
            return;
        }

        Add(new(target, state, streamId));
    }

    private void HoldStream(uint streamId)
    {
        foreach (var held in _held)
        {
            if (held.StreamId == streamId)
            {
                return;
            }
        }

        Add(new(Target: null, _manager.GetTransactionalState(streamId), streamId));
    }

    // The state's pending changes from before the transaction go into the journal, not the segment, so the journal
    // reproduces the state as it was when the transaction first held it.
    private void Add(HeldState held)
    {
        if (held.State is ICoalescingJournaledState coalescing)
        {
            _stagingToJournal = true;
            try
            {
                using var redirect = new RedirectScope(_manager.JournalWriter, redirect: null);
                coalescing.StagePendingChanges(_manager.JournalWriter.CreateJournalStreamWriter(new(held.StreamId)));
            }
            finally
            {
                _stagingToJournal = false;
            }
        }

        _held.Add(held);
    }

    private int IndexOf(IStateMachine state)
    {
        for (var i = 0; i < _held.Count; i++)
        {
            if (ReferenceEquals(_held[i].State, state))
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsHolding(JournaledStateAccessTarget target)
    {
        foreach (var held in _held)
        {
            if (ReferenceEquals(held.Target, target))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the transaction holds the state of target, including one which it holds because another state's operation
    // journaled the state's entries.
    private bool HoldsTarget(JournaledStateAccessTarget target)
    {
        var untargeted = false;
        foreach (var held in _held)
        {
            if (ReferenceEquals(held.Target, target))
            {
                return true;
            }

            untargeted |= held.Target is null;
        }

        return untargeted && Holds(_manager.GetTransactionalState(target).State);
    }

    private JournaledStateConflictException CreateConflict(JournaledStateAccessTarget target) =>
        new(_manager, _manager.GetTransactionalState(target).State, target.Name);

    private void ThrowIfEnded()
    {
        if (_ended)
        {
            throw new InvalidOperationException("The journaled state transaction has ended, so its code can't change journaled state.");
        }
    }

    // Sets the destination of the manager's journal entries for the synchronous scope of one operation.
    private readonly ref struct RedirectScope
    {
        private readonly JournalBufferWriter _writer;
        private readonly IJournalEntryRedirect? _previous;

        public RedirectScope(JournalBufferWriter writer, IJournalEntryRedirect? redirect)
        {
            _writer = writer;
            _previous = writer.Redirect;
            writer.Redirect = redirect;
        }

        public void Dispose() => _writer.Redirect = _previous;
    }

    // The context of code outside the transaction while it is open: writes to the transaction's states, and reads of them
    // which an inner context records, throw JournaledStateConflictException.
    private sealed class Guard(JournalTransaction transaction, JournaledStateContext? inner) : JournaledStateContext
    {
        protected override TResult Access<TArgument, TState, TResult>(
            in JournaledStateAccess access,
            TArgument argument,
            TState state,
            Func<TState, TResult> operation,
            Action<TState, TResult>? apply)
        {
            if (!transaction.HoldsTarget(access.Target))
            {
                return inner is null ? Run(state, operation, apply) : inner.AccessThrough(access, argument, state, operation, apply);
            }

            var isWrite = access.Kind is JournaledStateAccessKind.Write;
            if (inner is null)
            {
                return isWrite ? throw transaction.CreateConflict(access.Target) : Run(state, operation, apply);
            }

            // The check runs within the access, so a replayed outcome doesn't meet it, and a recording context sees the
            // conflict as the access's failure rather than its outcome.
            var target = access.Target;
            return inner.AccessThrough(
                access,
                argument,
                (State: state, Operation: operation, Transaction: transaction, Inner: inner, Target: target, IsWrite: isWrite),
                static held => held.IsWrite || held.Inner.RecordsAccesses
                    ? throw held.Transaction.CreateConflict(held.Target)
                    : held.Operation(held.State),
                apply is null ? null : (held, result) => apply(held.State, result));
        }
    }
}

/// <summary>
/// A state whose entries a transaction's segment receives: the access target through which the transaction wrote it, or
/// <see langword="null"/> when another state's operation journaled its entries, its state machine and its stream id.
/// </summary>
internal readonly record struct HeldState(JournaledStateAccessTarget? Target, IStateMachine State, uint StreamId);

/// <summary>
/// A state whose rebuild after an aborted transaction keeps what its journal doesn't hold, such as a log's open drafts,
/// in place of <see cref="IStateMachine.Reset"/>.
/// </summary>
internal interface IJournaledStateRollback
{
    /// <summary>Clears the state's journaled content before the manager replays its journal into it.</summary>
    void ResetForRollback(JournalStreamWriter writer);

    /// <summary>Called once the manager has replayed the journal, including its unwritten entries, into the state.</summary>
    void OnRollbackCompleted();
}

/// <summary>
/// A state which keeps a change in memory until a write journals it, such as a value: a transaction which holds it journals
/// the pending change through the state's stream.
/// </summary>
internal interface ICoalescingJournaledState
{
    /// <summary>Journals the state's pending change, if any, through <paramref name="writer"/>.</summary>
    void StagePendingChanges(JournalStreamWriter writer);
}
