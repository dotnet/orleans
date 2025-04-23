using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Interface for a state machine which can be persisted to durable storage.
/// </summary>
public interface IDurableStateMachine
{
    /// <summary>
    /// Resets the state machine.
    /// </summary>
    /// <remarks>
    /// If the state machine has any volatile state, it must be cleared by this method.
    /// This method can be called at any point in the state machine's lifetime, including during recovery.
    /// </remarks>
    void Reset(IStateMachineLogWriter storage);

    /// <summary>
    /// Called during recovery to apply the provided log entry or snapshot.
    /// </summary>
    /// <param name="entry">The log entry or snapshot.</param>
    void Apply(ReadOnlySequence<byte> entry);

    /// <summary>
    /// Notifies the state machine that all prior log entries and snapshots have been applied.
    /// </summary>
    /// <remarks>
    /// The state machine should not expect any additional <see cref="Apply"/> calls after this method is called,
    /// unless <see cref="Reset"/> is called to reset the state machine to its initial state.
    /// This method will be called before any <see cref="AppendEntries"/> or <see cref="AppendSnapshot"/> calls.
    /// </remarks>
    void OnRecoveryCompleted() { }

    /// <summary>
    /// Writes pending state changes to the log.
    /// </summary>
    /// <param name="writer">The log writer.</param>
    void AppendEntries(StateMachineStorageWriter writer);

    /// <summary>
    /// Writes a snapshot of the state machine to the provided writer.
    /// </summary>
    /// <param name="writer">The log writer.</param>
    void AppendSnapshot(StateMachineStorageWriter writer);

    /// <summary>
    /// Notifies the state machine that all prior log entries and snapshots which it has written have been written to stable storage.
    /// </summary>
    void OnWriteCompleted() { }

    /// <summary>
    /// Creates and returns a deep copy of this instance. All replicas must be independent such that changes to one do not affect any other.
    /// </summary>
    /// <returns>A replica of this instance.</returns>
    IDurableStateMachine DeepCopy();
}
