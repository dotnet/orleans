namespace Orleans.Journaling;

/// <summary>
/// Interface for a state which can be persisted to durable storage.
/// </summary>
public interface IJournaledState
{
    /// <summary>
    /// Gets the durable operation codec used by this state.
    /// </summary>
    object OperationCodec { get; }

    /// <summary>
    /// Resets the state.
    /// </summary>
    /// <remarks>
    /// If the state has any volatile state, it must be cleared by this method.
    /// This method can be called at any point in the state's lifetime, including during recovery.
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
