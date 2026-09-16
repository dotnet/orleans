namespace Orleans.Journaling;

/// <summary>
/// Interface for a state which can be persisted to durable storage.
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
/// When the application requests a write, the journaled state manager calls
/// <see cref="AppendEntries"/> (and occasionally <see cref="AppendSnapshot"/>) to materialize
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
public interface IJournaledState
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
    /// This method will be called before any <see cref="AppendEntries"/> or <see cref="AppendSnapshot"/> calls.
    /// </remarks>
    void OnRecoveryCompleted() { }

    /// <summary>
    /// Writes pending state changes to the journal.
    /// </summary>
    /// <param name="writer">The journal stream writer.</param>
    void AppendEntries(JournalStreamWriter writer);

    /// <summary>
    /// Writes a snapshot of the state to the provided writer.
    /// </summary>
    /// <param name="writer">The journal stream writer.</param>
    void AppendSnapshot(JournalStreamWriter writer);

    /// <summary>
    /// Notifies the state that all prior journal entries and snapshots which it has written have been written to stable storage.
    /// </summary>
    void OnWriteCompleted() { }

    /// <summary>
    /// Creates and returns a deep copy of this instance. All replicas must be independent such that changes to one do not affect any other.
    /// </summary>
    /// <returns>A replica of this instance.</returns>
    IJournaledState DeepCopy();
}
