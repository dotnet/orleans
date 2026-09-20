namespace Orleans.Journaling;

/// <summary>
/// Defines the replay, snapshot, and acknowledgement protocol for a journal-backed state machine.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are owned by a single <see cref="JournaledStateManager"/> and are accessed
/// from one logical thread at a time. They participate in the journaling lifecycle as follows:
/// </para>
/// <list type="bullet">
/// <item>
/// User code mutates in-memory state synchronously (typically by invoking codec helpers that
/// both apply the mutation locally and emit the corresponding command to the journal).
/// </item>
/// <item>
/// When the application requests a write, the journaled state manager validates the request,
/// validates the pending changes of all states, and calls
/// <see cref="WritePendingEntries"/> (and occasionally <see cref="WriteSnapshot"/>) to materialize
/// the pending changes, then flushes the journal to durable storage.
/// </item>
/// <item>
/// <see cref="OnWriteCompleted"/> is invoked when the durable write has been acknowledged.
/// Implementations that need to know when state has actually been persisted (for example,
/// to release waiters or trigger downstream notifications) should hook into this callback
/// rather than treating in-memory mutations as durable.
/// </item>
/// <item>
/// A failed journal operation permanently fences the manager and requests grain deactivation.
/// A new manager initializes new state instances by calling <see cref="Reset"/> and replaying durable entries.
/// </item>
/// </list>
/// <para>
/// Application code prepares fallible work in operation-local data and stages only mutations which are
/// safe to commit. Staged mutations are shared by all interleaved callers using the same manager.
/// Storage acknowledgement establishes durability; recovery takes place in a fresh manager and state instances.
/// </para>
/// </remarks>
public interface IStateMachine
{
    /// <summary>
    /// Replays one entry during journal recovery.
    /// </summary>
    /// <param name="entry">The entry to replay.</param>
    /// <param name="context">The replay context.</param>
    /// <remarks>
    /// Implementations must not retain <see cref="JournalEntry.Reader"/> or references to its
    /// backing storage after this method returns unless they copy the data.
    /// </remarks>
    void ReplayEntry(JournalEntry entry, JournalReplayContext context);

    /// <summary>
    /// Resets the state.
    /// </summary>
    /// <remarks>
    /// If the state has any volatile state, it must be cleared by this method.
    /// The manager calls this method when binding a stream, deleting the journal, or retiring a state.
    /// </remarks>
    void Reset(JournalStreamWriter writer);

    /// <summary>
    /// Notifies the state that all prior journal entries and snapshots have been applied.
    /// </summary>
    /// <remarks>
    /// The state should not expect any additional recovery entries after this method is called,
    /// unless <see cref="Reset"/> is called to reset the state to its initial state.
    /// This method will be called before any <see cref="WritePendingEntries"/> or <see cref="WriteSnapshot"/> calls.
    /// </remarks>
    void OnRecoveryCompleted() { }

    /// <summary>
    /// Validates this state's pending changes immediately before an admitted write captures journal entries or a snapshot.
    /// The default implementation accepts the pending changes.
    /// </summary>
    /// <remarks>
    /// Validation is pure and synchronous. All registered states pass validation before any state is captured,
    /// including writes which only flush committed entries or produce zero bytes. Validation and capture run
    /// in the same work-loop continuation. Throwing reports a terminal state-local failure and fences the manager.
    /// Callers acquire asynchronous prerequisites before staging changes. Independent operation-local preparation
    /// can proceed while previously staged valid changes are captured.
    /// </remarks>
    void ValidatePendingChanges() { }

    /// <summary>
    /// Validates a write request in the public caller's context before it is queued.
    /// The default implementation accepts the request.
    /// </summary>
    /// <remarks>
    /// Validation is pure and runs only at request admission. Throwing rejects this request and leaves the manager healthy.
    /// Use <see cref="ValidatePendingChanges"/> to report a terminal state-local failure inside admitted execution.
    /// </remarks>
    void ValidateWrite() { }

    /// <summary>
    /// Validates deletion at public request admission and again during serialized execution.
    /// The default implementation accepts deletion.
    /// </summary>
    /// <remarks>
    /// Validation is pure. An admission failure rejects the request and leaves the manager healthy.
    /// An execution-time failure fences the manager. All states pass execution-time validation
    /// before the manager calls <see cref="OnDeleteStarted"/> on any state.
    /// </remarks>
    void ValidateDelete() { }

    /// <summary>
    /// Notifies the state that deletion is starting, after all execution-time validation succeeds
    /// and before the storage operation begins. The default implementation performs no action.
    /// </summary>
    /// <remarks>
    /// A successful storage deletion is followed by <see cref="Reset"/> before deletion waiters complete.
    /// </remarks>
    void OnDeleteStarted() { }

    /// <summary>
    /// Notifies the state of the manager's first terminal failure, before current and queued operation waiters fault.
    /// The default implementation performs no action.
    /// </summary>
    /// <param name="exception">The original failure recorded by the manager.</param>
    /// <remarks>
    /// The manager is already fenced when this callback runs. Every registered state is notified even if
    /// another notification throws; notification errors are logged and the original failure is preserved.
    /// Owner shutdown during initial recovery and idle shutdown complete through normal shutdown.
    /// Cancellation during admitted validation or write/delete storage work is terminal.
    /// </remarks>
    void OnFaulted(Exception exception) { }

    /// <summary>
    /// Writes pending state changes to the journal.
    /// </summary>
    /// <param name="writer">The journal stream writer.</param>
    void WritePendingEntries(JournalStreamWriter writer);

    /// <summary>
    /// Writes a snapshot of the state to the provided writer.
    /// </summary>
    /// <param name="writer">The journal stream writer.</param>
    void WriteSnapshot(JournalStreamWriter writer);

    /// <summary>
    /// Notifies the state that all prior journal entries and snapshots which it has written have been written to stable storage.
    /// </summary>
    void OnWriteCompleted() { }
}
