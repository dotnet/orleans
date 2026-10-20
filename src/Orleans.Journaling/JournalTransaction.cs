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
/// The segment needs no markers: it joins the journal buffer whole, and the write which captures it reaches storage in
/// one append, which storage applies atomically. Recovery therefore finds all of a committed transaction's entries or
/// none of them. A durable transaction's effect record is one of its entries.
/// </para>
/// <para>
/// The transaction is the context of the code which runs in its flow. A write holds its state: the state's pending changes
/// from before the transaction are staged into the journal first, so the journal reproduces the state as it was, and the
/// write's entries go into the segment. Reads run against live state.
/// </para>
/// <para>
/// Other code sees a guard over its own context while the transaction is open. It reads the transaction's uncommitted
/// changes, as an interleaving request sees any request's intermediate state, and its writes to held states throw.
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

    // The states which the transaction wrote. A transaction writes few states, so linear searches beat hashing.
    private readonly List<HeldState> _held = [];
    private JournalBufferWriter? _segment;
    private Guard? _directGuard;
    private Exception? _doom;
    private bool _ended;

    public JournalTransaction(JournaledStateManager manager) => _manager = manager;

    /// <summary>Gets or sets the transaction in whose flow the calling code runs, or <see langword="null"/>.</summary>
    internal static JournalTransaction? Current
    {
        get => CurrentTransaction.Value;
        set => CurrentTransaction.Value = value;
    }

    /// <summary>Gets the manager whose states the transaction covers.</summary>
    public JournaledStateManager Manager => _manager;

    /// <summary>Gets the states which the transaction wrote, with their stream ids.</summary>
    public IReadOnlyList<HeldState> HeldStates => _held;

    /// <summary>Gets the exception which escaped a nested transaction, which aborts this one when it ends.</summary>
    public Exception? DoomedBy => _doom;

    /// <summary>Gets whether the transaction holds <paramref name="state"/>.</summary>
    public bool Holds(IStateMachine state)
    {
        foreach (var held in _held)
        {
            if (ReferenceEquals(held.State, state))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks the transaction to abort when it ends, because <paramref name="exception"/> escaped a nested one.</summary>
    public void Doom(Exception exception) => _doom ??= exception;

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
        Hold(access.Target);
        var writer = _manager.JournalWriter;
        var previous = writer.Redirect;
        writer.Redirect = this;
        try
        {
            return Run(state, operation, apply);
        }
        finally
        {
            writer.Redirect = previous;
        }
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
        return _segment ??= _manager.CreateTransactionWriter();
    }

    /// <summary>
    /// Commits the transaction in one synchronous block: runs <paramref name="onCommit"/> in the transaction, stages the held
    /// states' pending values into the segment, and appends the segment to the manager's journal, so the next write which
    /// storage appends atomically holds all of the transaction's entries or none of them.
    /// </summary>
    /// <remarks>The caller ends the transaction afterwards; a failure leaves the segment unappended, for an abort.</remarks>
    public void Commit(Action? onCommit)
    {
        ThrowIfEnded();
        onCommit?.Invoke();

        var writer = _manager.JournalWriter;
        var previous = writer.Redirect;
        writer.Redirect = this;
        try
        {
            foreach (var held in _held)
            {
                if (held.State is ICoalescingJournaledState coalescing)
                {
                    coalescing.StagePendingChanges(writer.CreateJournalStreamWriter(new(held.StreamId)));
                }
            }
        }
        finally
        {
            writer.Redirect = previous;
        }

        if (_segment is { } segment)
        {
            writer.AppendCommitted(segment);
        }
    }

    /// <summary>Ends the transaction: returns its segment to the manager, after which its writes throw.</summary>
    public void End()
    {
        _ended = true;
        if (_segment is { } segment)
        {
            _segment = null;
            _manager.ReturnTransactionWriter(segment);
        }
    }

    // Holds the target's state for the transaction. The state's pending changes from before the transaction go into the
    // journal, not the segment, so the journal reproduces the state as it was when the transaction first wrote it.
    private void Hold(JournaledStateAccessTarget target)
    {
        if (IsHolding(target))
        {
            return;
        }

        var (state, streamId) = _manager.GetTransactionalState(target);
        _held.Add(new(target, state, streamId));
        if (state is ICoalescingJournaledState coalescing)
        {
            var writer = _manager.JournalWriter;
            var previous = writer.Redirect;
            writer.Redirect = null;
            try
            {
                coalescing.StagePendingChanges(writer.CreateJournalStreamWriter(new(streamId)));
            }
            finally
            {
                writer.Redirect = previous;
            }
        }
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

    private void ThrowIfEnded()
    {
        if (_ended)
        {
            throw new InvalidOperationException("The journaled state transaction has ended, so its code can't change journaled state.");
        }
    }

    // The context of code outside the transaction while it is open: writes to the transaction's states throw.
    private sealed class Guard(JournalTransaction transaction, JournaledStateContext? inner) : JournaledStateContext
    {
        protected override TResult Access<TArgument, TState, TResult>(
            in JournaledStateAccess access,
            TArgument argument,
            TState state,
            Func<TState, TResult> operation,
            Action<TState, TResult>? apply)
        {
            if (access.Kind is JournaledStateAccessKind.Write && transaction.IsHolding(access.Target))
            {
                throw new InvalidOperationException(
                    $"Journaled state '{access.Target.Name}' is held by an open transaction: only the transaction's code can change it until the transaction ends.");
            }

            return inner is null ? Run(state, operation, apply) : inner.AccessThrough(access, argument, state, operation, apply);
        }
    }
}

/// <summary>A state which a transaction wrote: its access target, its state machine and its stream id.</summary>
internal readonly record struct HeldState(JournaledStateAccessTarget Target, IStateMachine State, uint StreamId);

/// <summary>
/// A state whose rebuild after an aborted transaction keeps what its journal doesn't hold, such as a log's open drafts,
/// in place of <see cref="IStateMachine.Reset"/>.
/// </summary>
internal interface IJournaledStateRollback
{
    /// <summary>Clears the state's journaled content before the manager replays its journal into it.</summary>
    void ResetForRollback(JournalStreamWriter writer);
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
