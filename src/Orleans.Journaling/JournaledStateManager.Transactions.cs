using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal partial class JournaledStateManager
{
    // The open transaction, or null. At most one is open at a time.
    private JournalTransaction? _transaction;

    // The segment writer of the last transaction which ended, which the next one reuses.
    private JournalBufferWriter? _idleTransactionWriter;

    // Completes when the open transaction ends, for code which waits for its states' release.
    private TaskCompletionSource? _transactionEnded;

    /// <summary>Completes once no open transaction holds <paramref name="state"/>.</summary>
    /// <remarks>
    /// A watch starts after the transaction which holds its state, so its initial content is never uncommitted. A watch which
    /// the transaction's own code starts starts after the transaction ends too.
    /// </remarks>
    internal async ValueTask WaitUntilReleasedAsync(IStateMachine state, CancellationToken cancellationToken)
    {
        while (IsHeldByTransaction(state))
        {
            Task ended;
            lock (_lock)
            {
                ended = (_transactionEnded ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

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

    /// <summary>Gets the writer of a transaction's segment, in the journal's format.</summary>
    internal JournalBufferWriter CreateTransactionWriter()
    {
        var writer = _idleTransactionWriter ?? _shared.JournalFormat.CreateWriter();
        _idleTransactionWriter = null;
        return writer;
    }

    /// <summary>Returns a transaction's segment writer, which the next transaction reuses.</summary>
    internal void ReturnTransactionWriter(JournalBufferWriter writer)
    {
        writer.Reset();
        if (_idleTransactionWriter is null)
        {
            _idleTransactionWriter = writer;
        }
        else
        {
            writer.Dispose();
        }
    }

    /// <summary>Gets whether the open transaction holds <paramref name="state"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsHeldByTransaction(IStateMachine state) => _transaction is { } transaction && transaction.Holds(state);

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

    /// <inheritdoc cref="RunTransactionAsync{T}(Func{CancellationToken, Task{T}}, Action{T}?, CancellationToken)"/>
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
            cancellationToken);
    }

    /// <inheritdoc cref="RunTransactionAsync{T}(Func{CancellationToken, Task{T}}, Action{T}?, CancellationToken)"/>
    internal Task<T> RunTransactionAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken = default) =>
        RunTransactionAsync(body, onCommit: null, cancellationToken);

    /// <summary>
    /// Runs <paramref name="body"/> as a transaction over this manager's states: its changes commit together when it
    /// returns, or none of them do when it throws.
    /// </summary>
    /// <param name="body">The transaction's code.</param>
    /// <param name="onCommit">
    /// Runs in the transaction's commit, with the body's result, so the changes it makes commit with the body's. An exception
    /// it throws aborts the transaction.
    /// </param>
    /// <param name="cancellationToken">
    /// The token passed to <paramref name="body"/>, which also cancels the wait for other transactions to end.
    /// </param>
    /// <returns>The body's result, once the transaction has committed.</returns>
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
    /// throw. The commit is logical: the changes become durable with the next journal write, as every change does, and
    /// effects which depend on durability, such as outbox deliveries, wait for that write. An abort leaves no trace in the
    /// journal and rebuilds the states which the body changed from the journal.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The caller runs on another thread than the activation's scheduler, for example after <c>ConfigureAwait(false)</c>.
    /// </exception>
    /// <exception cref="NotSupportedException">The manager's grain context can't open exclusive sections.</exception>
    internal Task<T> RunTransactionAsync<T>(Func<CancellationToken, Task<T>> body, Action<T>? onCommit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (JournalTransaction.Current is { } outer && ReferenceEquals(outer, _transaction))
        {
            if (onCommit is not null)
            {
                throw new NotSupportedException("A transaction nested in another transaction of the same journal can't run code at its commit.");
            }

            return RunNestedTransactionAsync(outer, body, cancellationToken);
        }

        if (JournalTransaction.Current is { } other && ReferenceEquals(other.Manager.OpenTransaction, other))
        {
            // A transaction of another journal of the activation holds the activation exclusively already.
            return RunTransactionCoreAsync(body, onCommit, cancellationToken);
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

            return RunTransactionInSectionAsync(admission, body, onCommit, cancellationToken);
        }

        // A manager without a grain context relies on its callers to serialize their accesses.
        return RunTransactionCoreAsync(body, onCommit, cancellationToken);
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

    private async Task<T> RunTransactionInSectionAsync<T>(
        IGrainRequestAdmission admission,
        Func<CancellationToken, Task<T>> body,
        Action<T>? onCommit,
        CancellationToken cancellationToken)
    {
        using var section = await admission.EnterExclusiveSectionAsync(cancellationToken);
        return await RunTransactionCoreAsync(body, onCommit, cancellationToken);
    }

    private async Task<T> RunTransactionCoreAsync<T>(
        Func<CancellationToken, Task<T>> body,
        Action<T>? onCommit,
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
        try
        {
            result = await body(cancellationToken);
            if (transaction.DoomedBy is { } doom)
            {
                throw new InvalidOperationException("A transaction nested in this transaction failed, so this transaction aborted.", doom);
            }

            transaction.Commit(onCommit is null ? null : () => onCommit(result));
        }
        catch
        {
            await AbortTransactionAsync(transaction);
            throw;
        }

        EndTransaction(transaction);
        return result;
    }

    private JournalTransaction BeginTransaction()
    {
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
            return transaction;
        }
    }

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

    private async ValueTask AbortTransactionAsync(JournalTransaction transaction)
    {
        transaction.End();
        if (transaction.HeldStates.Count == 0)
        {
            EndTransaction(transaction);
            return;
        }

        Task task;
        lock (_lock)
        {
            var rollback = new RollbackWorkItem(transaction)
            {
                EnqueuedTimestamp = _shared.TimeProvider.GetTimestamp(),
                DeletionGeneration = _deletionGeneration,
            };

            _workQueue.Enqueue(rollback);
            task = rollback.Task;
        }

        _workSignal.Signal();
        try
        {
            await task;
        }
        finally
        {
            // A failed rollback fences the manager, which deactivates its activation; the transaction ends either way.
            EndTransaction(transaction);
        }
    }

    // Releases the transaction's states, which then take part in captures and acknowledgements again, and publishes their
    // content to their watchers, which saw none of the transaction's changes.
    private void EndTransaction(JournalTransaction transaction)
    {
        TaskCompletionSource? ended;
        lock (_lock)
        {
            Debug.Assert(ReferenceEquals(_transaction, transaction));
            transaction.End();
            _transaction = null;
            ended = _transactionEnded;
            _transactionEnded = null;
        }

        ended?.TrySetResult();

        if (Observers is { } observers)
        {
            foreach (var (_, state, _) in transaction.HeldStates)
            {
                if (state is IJournaledStateResync resync && observers.TryGetTopic(state, out var topic))
                {
                    resync.PublishResync(topic);
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
                "A transaction's code can't write journaled state: the transaction's changes join the journal when it commits, and become durable with the next write after it.");
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
