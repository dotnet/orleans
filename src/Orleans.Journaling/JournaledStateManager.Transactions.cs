using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal partial class JournaledStateManager
{
    // The open transaction, or null. At most one is open at a time. The work loop reads it while capturing writes, which for
    // a manager without an activation runs on another thread, so it changes under the lock.
    private JournalTransaction? _transaction;

    // Completes when the open transaction ends, for code which waits for its states' release. Used by the transaction's
    // callers only, which run on the activation's scheduler.
    private TaskCompletionSource? _transactionEnded;

    /// <summary>Completes once no open transaction holds <paramref name="state"/>.</summary>
    /// <remarks>
    /// A watch starts after the transaction which holds its state, so its initial content is never uncommitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The calling code runs in the transaction which holds <paramref name="state"/>, which would wait for its own end.
    /// </exception>
    internal async ValueTask WaitUntilReleasedAsync(IStateMachine state, CancellationToken cancellationToken)
    {
        while (_transaction is { } transaction && transaction.Holds(state))
        {
            if (ReferenceEquals(JournalTransaction.Current, transaction))
            {
                throw new InvalidOperationException(
                    "A transaction's code can't watch state which the transaction changed: the watch starts when the transaction ends.");
            }

            var ended = (_transactionEnded ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            await ended.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Gets the context in which the calling code accesses this manager's states, or <see langword="null"/>.</summary>
    /// <remarks>
    /// While a transaction is open, it is the transaction for code in its flow and a guard over the host's context for other
    /// code. Otherwise it's the host's context.
    /// </remarks>
    internal JournaledStateContext? CurrentContext
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _transaction is { } transaction ? transaction.GetContext(ContextProvider) : ContextProvider?.Current;
    }

    /// <summary>Gets the writer which buffers this manager's committed journal entries.</summary>
    internal JournalBufferWriter JournalWriter => _journalWriter;

    /// <summary>Gets the open transaction, or <see langword="null"/>.</summary>
    internal JournalTransaction? OpenTransaction => _transaction;

    /// <summary>Creates the writer of a transaction's segment, in the journal's format.</summary>
    internal JournalBufferWriter CreateTransactionWriter() => _shared.JournalFormat.CreateWriter();

    /// <summary>Gets whether the open transaction holds <paramref name="state"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsHeldByTransaction(IStateMachine state) => _transaction is { } transaction && transaction.Holds(state);

    /// <summary>Gets whether the open transaction holds <paramref name="state"/> and the calling code runs in it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsHeldByCurrentTransaction(IStateMachine state) =>
        _transaction is { } transaction && transaction.Holds(state) && ReferenceEquals(JournalTransaction.Current, transaction);

    /// <summary>
    /// Gets whether <paramref name="state"/>, a state which journals its changes when asked, may stage its pending changes
    /// now: unless the open transaction holds it, and then only into the transaction's segment.
    /// </summary>
    /// <remarks>
    /// A held state's pending value is the transaction's uncommitted change, which reaches the journal only through the
    /// transaction's commit; journal captures skip held states. States assert this when they stage.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool MayStagePendingChanges(IStateMachine state) =>
        _transaction is not { } transaction || ReferenceEquals(_journalWriter.Redirect, transaction) || !transaction.Holds(state);

    /// <summary>Gets the state machine and stream id of the state which <paramref name="target"/> identifies.</summary>
    internal (IStateMachine State, uint StreamId) GetTransactionalState(JournaledStateAccessTarget target)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(target.Name, out var state) && _journalStreamDirectory.TryGetValue(target.Name, out var streamId))
            {
                return (state, streamId);
            }
        }

        throw new InvalidOperationException($"Journaled state '{target.Name}' isn't registered with this manager, so a transaction can't change it.");
    }

    /// <summary>Gets the state machine whose entries journal stream <paramref name="streamId"/> holds.</summary>
    internal IStateMachine GetTransactionalState(uint streamId)
    {
        lock (_lock)
        {
            if (_statesMap.TryGetValue(streamId, out var state))
            {
                return state;
            }
        }

        throw new InvalidOperationException($"Journal stream {streamId} belongs to no state of this manager, so a transaction can't change it.");
    }

    /// <inheritdoc cref="RunTransactionAsync{T}(Func{CancellationToken, Task{T}}, Action{T}?, Action{Exception}?, CancellationToken)"/>
    internal Task RunTransactionAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return RunTransactionAsync<object?>(
            async cancellationToken =>
            {
                await body(cancellationToken);
                return null;
            },
            onCommit: null,
            onAbort: null,
            cancellationToken);
    }

    /// <inheritdoc cref="RunTransactionAsync{T}(Func{CancellationToken, Task{T}}, Action{T}?, Action{Exception}?, CancellationToken)"/>
    internal Task<T> RunTransactionAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken = default) =>
        RunTransactionAsync(body, onCommit: null, onAbort: null, cancellationToken);

    /// <summary>
    /// Runs <paramref name="body"/> as a transaction over this manager's states: its changes commit together when it
    /// returns, or none of them do when it throws.
    /// </summary>
    /// <param name="body">The transaction's code.</param>
    /// <param name="onCommit">
    /// Runs in the transaction's commit, with the body's result, so the changes it makes commit with the body's. An exception
    /// it throws aborts the transaction.
    /// </param>
    /// <param name="onAbort">
    /// Runs once the transaction has aborted and its states are rebuilt, with the exception which the call throws, so the
    /// changes it makes join the journal as ordinary changes. It doesn't run when the manager can't abort the transaction,
    /// because the journal failed or the manager is stopping.
    /// </param>
    /// <param name="cancellationToken">
    /// The token passed to <paramref name="body"/>, which also cancels the wait for other transactions to end.
    /// </param>
    /// <returns>The body's result, once a journal write which holds the transaction's commit has completed.</returns>
    /// <remarks>
    /// <para>
    /// The body runs exclusively on the activation, in the caller's flow, within an exclusive section
    /// (<see cref="IGrainRequestAdmission.EnterExclusiveSectionAsync"/>): no request starts while it runs, except requests
    /// which always interleave and requests on the caller's call chain. A grain method can await it. On a reentrant grain,
    /// requests which were already running can resume alongside it. Transactions on one activation run one at a time. Called
    /// from a transaction's body, it runs as part of that transaction, and an exception which escapes it aborts that
    /// transaction too. The caller runs on the activation's scheduler. A manager without a grain context runs the body
    /// directly, and its callers serialize their accesses.
    /// </para>
    /// <para>
    /// Code which runs alongside the body reads its uncommitted changes, and its writes to states which the body changed
    /// throw. The commit is a durable prepare: the call returns once a journal write which holds the transaction's entries
    /// has completed, and effects which depend on durability, such as outbox deliveries, follow that write. The exclusive
    /// section ends at the commit, so transactions which commit while the write is pending share it. When the write fails,
    /// the call throws its exception, and the journal's failure deactivates the activation, whose next activation recovers
    /// none of the transaction. An abort leaves no trace in the journal and rebuilds the states which the body changed from
    /// the journal.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The caller runs on another thread than the activation's scheduler, for example after <c>ConfigureAwait(false)</c>, or
    /// in code which a durable task records, whose transactions run through <c>DurableTaskTransactions.RunAsync</c>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The caller runs in a transaction of another manager, or the manager's grain context can't open exclusive sections.
    /// </exception>
    internal Task<T> RunTransactionAsync<T>(
        Func<CancellationToken, Task<T>> body,
        Action<T>? onCommit,
        Action<Exception>? onAbort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (JournalTransaction.Current is { } current && ReferenceEquals(current.Manager.OpenTransaction, current))
        {
            if (!ReferenceEquals(current.Manager, this))
            {
                // Each journal commits in its own append, so the nested transaction would become durable on its own and
                // survive the abort of the transaction around it.
                throw new NotSupportedException("A transaction can't run inside a transaction over another journaled state manager's states.");
            }

            if (onCommit is not null || onAbort is not null)
            {
                throw new NotSupportedException("A transaction nested in another transaction of the same journal can't run code at its commit or abort.");
            }

            return RunNestedTransactionAsync(current, body, cancellationToken);
        }

        if (ContextProvider?.Current is not null)
        {
            // The host records the calling code's accesses and replays them, which would apply the transaction's changes again.
            throw new InvalidOperationException(
                "A durable task body runs a transaction over journaled state through DurableTaskTransactions.RunAsync, which records its outcome.");
        }

        if (_grainContext is { } grainContext)
        {
            if (grainContext.GetComponent<IGrainRequestAdmission>() is not { } admission)
            {
                throw new NotSupportedException(
                    $"The grain context '{grainContext}' can't run code exclusively ({nameof(IGrainRequestAdmission)}), so a transaction over its journaled state can't run.");
            }

            if (!ReferenceEquals(RuntimeContext.Current, grainContext))
            {
                throw new InvalidOperationException(
                    "A transaction over a grain's journaled state runs on the grain activation's scheduler. Code which continues elsewhere, for example after ConfigureAwait(false), can't run one.");
            }

            return RunExclusiveTransactionAsync(admission, body, onCommit, onAbort, cancellationToken);
        }

        // A manager without a grain context relies on its callers to serialize their accesses.
        return RunExclusiveTransactionAsync(admission: null, body, onCommit, onAbort, cancellationToken);
    }

    private static async Task<T> RunNestedTransactionAsync<T>(JournalTransaction outer, Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        try
        {
            return await body(cancellationToken);
        }
        catch (Exception exception)
        {
            outer.Doom(exception);
            throw;
        }
    }

    private async Task<T> RunExclusiveTransactionAsync<T>(
        IGrainRequestAdmission? admission,
        Func<CancellationToken, Task<T>> body,
        Action<T>? onCommit,
        Action<Exception>? onAbort,
        CancellationToken cancellationToken)
    {
        (T Result, Task Durable) committed;
        var section = admission is null ? null : await admission.EnterExclusiveSectionAsync(cancellationToken);
        try
        {
            committed = await RunTransactionCoreAsync(body, onCommit, onAbort, cancellationToken);
        }
        finally
        {
            section?.Dispose();
        }

        // The commit's write may still be pending: transactions which commit meanwhile join it. The caller learns its outcome
        // whether or not it is canceled meanwhile, since the commit is part of the journal either way.
        await committed.Durable;
        return committed.Result;
    }

    private async Task<(T Result, Task Durable)> RunTransactionCoreAsync<T>(
        Func<CancellationToken, Task<T>> body,
        Action<T>? onCommit,
        Action<Exception>? onAbort,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_migrationSnapshotRequired)
        {
            // A snapshot can't pass an open transaction, so the one which the journal needs is written first.
            await WriteStateAsync(cancellationToken);
        }

        var transaction = BeginTransaction();
        JournalTransaction.Current = transaction;
        T result;
        bool appended;
        try
        {
            result = await body(cancellationToken);
            if (transaction.DoomedBy is { } doom)
            {
                throw new InvalidOperationException("The transaction was doomed to abort, so it aborted.", doom);
            }

            lock (_lock)
            {
                ThrowIfStateOperationsUnavailable();
            }

            appended = transaction.Commit(onCommit is null ? null : () => onCommit(result));
        }
        catch (Exception exception)
        {
            // Throws the journal's failure instead when the manager can't abort the transaction.
            await AbortTransactionAsync(transaction);
            onAbort?.Invoke(exception);
            throw;
        }

        Task durable;
        try
        {
            durable = appended ? EnqueueCommitWrite() : Task.CompletedTask;
        }
        finally
        {
            EndTransaction(transaction);
        }

        return (result, durable);
    }

    private JournalTransaction BeginTransaction()
    {
        AssertOnActivationScheduler();
        lock (_lock)
        {
            ThrowIfStateOperationsUnavailable();
            if (_transaction is not null)
            {
                throw new InvalidOperationException("Another transaction is open on this journaled state manager.");
            }

            if (_positions.IsDeleting || HasQueuedDeletion())
            {
                throw new InvalidOperationException("The journaled state is being deleted, so a transaction can't begin.");
            }

            Debug.Assert(!_migrationSnapshotRequired, "A transaction begins after the snapshot which the journal needs.");
            var transaction = new JournalTransaction(this);
            _transaction = transaction;
            _journalWriter.Transaction = transaction;
            return transaction;
        }
    }

    // A transaction's state belongs to the activation's scheduler, which runs every access to its states.
    [Conditional("DEBUG")]
    private void AssertOnActivationScheduler() =>
        Debug.Assert(_grainContext is null || ReferenceEquals(RuntimeContext.Current, _grainContext), "Transactions run on the activation's scheduler.");

    // Called under the lock.
    private bool HasQueuedDeletion()
    {
        foreach (var workItem in _workQueue)
        {
            if (workItem is DeleteStateWorkItem)
            {
                return true;
            }
        }

        return false;
    }

    // Joins the journal write which a caller already queued, or queues one: the queued write captures the journal buffer, and
    // with it the commit, after the commit appended its segment. A write which fails fences the manager.
    private Task EnqueueCommitWrite()
    {
        WorkItem write;
        bool didEnqueue;
        lock (_lock)
        {
            ThrowIfStateOperationsUnavailable();
            write = EnqueueOrGetPendingWrite(out didEnqueue, out _);
            write.CarriesCommit = true;
        }

        if (didEnqueue)
        {
            _workSignal.Signal();
        }

        return write.Task;
    }

    private async ValueTask AbortTransactionAsync(JournalTransaction transaction)
    {
        try
        {
            // Ends the transaction first, so its code can't change the states while they're rebuilt.
            transaction.End();
            Task rollback;
            lock (_lock)
            {
                // A fenced or stopping manager runs no more work, so the rollback would never run: the activation recovers
                // from the journal instead, which doesn't hold the transaction.
                ThrowIfStateOperationsUnavailable();
                if (transaction.HeldStates.Count == 0)
                {
                    return;
                }

                var item = new RollbackWorkItem(transaction)
                {
                    EnqueuedTimestamp = _shared.TimeProvider.GetTimestamp(),
                    DeletionGeneration = _deletionGeneration,
                };

                _workQueue.Enqueue(item);
                rollback = item.Task;
            }

            _workSignal.Signal();

            // A failed rollback fences the manager, which deactivates its activation.
            await rollback;
        }
        finally
        {
            EndTransaction(transaction);
        }
    }

    // Releases the transaction's states, which then take part in captures and acknowledgements again, and publishes their
    // content to their watchers, which saw none of the transaction's changes.
    private void EndTransaction(JournalTransaction transaction)
    {
        Debug.Assert(ReferenceEquals(_transaction, transaction));
        AssertOnActivationScheduler();
        try
        {
            transaction.End();
        }
        finally
        {
            lock (_lock)
            {
                _transaction = null;
                _journalWriter.Transaction = null;
            }

            var ended = _transactionEnded;
            _transactionEnded = null;
            ended?.TrySetResult();
            PublishResyncs(transaction);
        }
    }

    // The transaction's outcome stands whether or not its watchers can be told: a watcher which can't receive its state's
    // content ends with the failure rather than miss the change.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Publication follows the transaction's outcome and can't change it.")]
    private void PublishResyncs(JournalTransaction transaction)
    {
        if (Observers is not { } observers)
        {
            return;
        }

        foreach (var (_, state, _) in transaction.HeldStates)
        {
            if (state is IJournaledStateResync resync && observers.TryGetTopic(state, out var topic))
            {
                try
                {
                    resync.PublishResync(topic);
                }
                catch (Exception exception)
                {
                    topic.Complete(exception);
                }
            }
        }
    }

    // Rebuilds the aborted transaction's states from the journal: storage's history, then the entries which this manager
    // committed but hasn't written. Called by the work loop, so no write is in flight. The history read leaves storage's
    // write precondition unchanged, and fences the manager when another writer changed the journal. The states stay held
    // until the transaction ends.
    private async ValueTask RollbackAsync(JournalTransaction transaction, CancellationToken cancellationToken)
    {
        using var history = new JournalRollbackReplay(this, transaction.HeldStates);
        await ReadHistoryAsync(history, cancellationToken).ConfigureAwait(true);
        lock (_lock)
        {
            history.Rebuild();

            // The states' durable content is storage's: they publish it as recovered, and the entries which follow storage's
            // content replay as unwritten changes.
            foreach (var (_, state, _) in transaction.HeldStates)
            {
                state.OnRecoveryCompleted();
            }

            using var committed = _journalWriter.GetCommittedBuffer();
            history.Replay(committed, WriteJournalFormatKey);
        }
    }
    /// <summary>Throws when the calling code runs in this manager's open transaction, whose commit journals its changes.</summary>
    private void ThrowIfInTransaction()
    {
        if (_transaction is { } transaction && ReferenceEquals(JournalTransaction.Current, transaction))
        {
            throw new InvalidOperationException(
                "A transaction's code can't write journaled state: the transaction's changes join the journal when it commits.");
        }
    }

    /// <summary>
    /// Throws when the calling code runs in this manager's open transaction, for state which doesn't take part in
    /// transactions, such as <c>IPersistentState</c>, whose changes would survive the transaction's abort.
    /// </summary>
    internal void ThrowIfInTransaction(string stateDescription)
    {
        if (_transaction is { } transaction && ReferenceEquals(JournalTransaction.Current, transaction))
        {
            throw new InvalidOperationException(
                $"{stateDescription} doesn't take part in transactions, so a transaction's code can't use it: its changes would survive the transaction's abort.");
        }
    }

    private sealed class RollbackWorkItem(JournalTransaction transaction) : WorkItem
    {
        public JournalTransaction Transaction { get; } = transaction;
    }

    /// <summary>
    /// Copies storage's history, then resets an aborted transaction's states and replays their entries into them, in the
    /// format in which storage holds each part of the history.
    /// </summary>
    private sealed class JournalRollbackReplay(JournaledStateManager manager, IReadOnlyList<HeldState> states)
        : IJournalStorageConsumer, IJournalReplayTarget, IDisposable
    {
        private readonly Dictionary<uint, IStateMachine> _streams = states.ToDictionary(static held => held.StreamId, static held => held.State);
        private readonly IgnoredState _ignored = new();

        // Storage's history, as consecutive runs of content in one format. The states keep their transactional content until
        // the whole history is read, so the history replays only once the read completes.
        private readonly List<(ArcBufferWriter Content, string FormatKey)> _history = [];

        string IJournalReplayTarget.WriteJournalFormatKey => manager.WriteJournalFormatKey;

        IServiceProvider IJournalReplayTarget.ServiceProvider => manager.ServiceProvider;

        void IJournalStorageConsumer.Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            var length = buffer.Length;
            if (length == 0)
            {
                return;
            }

            var formatKey = manager.GetStoredJournalFormatKey(metadata);
            if (_history.Count == 0 || !string.Equals(_history[^1].FormatKey, formatKey, StringComparison.Ordinal))
            {
                _history.Add((new ArcBufferWriter(), formatKey));
            }

            using var slice = buffer.Peek(length);
            _history[^1].Content.Write(slice.AsReadOnlySequence());
            buffer.Skip(length);
        }

        /// <summary>Resets the states and replays storage's entries of their streams into them.</summary>
        public void Rebuild()
        {
            foreach (var (streamId, state) in _streams)
            {
                var writer = manager.CreateJournalStreamWriter(new(streamId));
                if (state is IJournaledStateRollback rollback)
                {
                    rollback.ResetForRollback(writer);
                }
                else
                {
                    state.Reset(writer);
                }
            }

            foreach (var (content, formatKey) in _history)
            {
                manager.Replay(new JournalBufferReader(content.Reader, isCompleted: true), formatKey, this);
            }
        }

        /// <summary>
        /// Replays <paramref name="entries"/>, whole entries in the format of <paramref name="formatKey"/> which follow the
        /// history: the entries which the manager committed but hasn't written.
        /// </summary>
        public void Replay(ArcBuffer entries, string formatKey)
        {
            if (entries.Length == 0)
            {
                return;
            }

            using var buffer = new ArcBufferWriter();
            buffer.AppendPinned(entries);
            manager.Replay(new JournalBufferReader(buffer.Reader, isCompleted: true), formatKey, this);
        }

        IStateMachine IJournalReplayTarget.ResolveStateMachine(JournalStreamId streamId) =>
            _streams.TryGetValue(streamId.Value, out var state) ? state : _ignored;

        public void Dispose()
        {
            foreach (var (content, _) in _history)
            {
                content.Dispose();
            }
        }

        private sealed class IgnoredState : IStateMachine
        {
            void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) { }

            void IStateMachine.Reset(JournalStreamWriter writer) { }

            void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => throw new NotSupportedException();

            void IStateMachine.WriteSnapshot(JournalStreamWriter writer) => throw new NotSupportedException();
        }
    }
}
