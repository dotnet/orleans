using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides functionality for writing out-of-band log entries to the log for the state machine which holds this instance.
/// </summary>
public interface IStateMachineLogWriter
{
    /// <summary>
    /// Appends an entry to the log for the state machine which holds this instance.
    /// </summary>
    /// <typeparam name="TState">The state, passed to the <paramred name="action"/> delegate.</typeparam>
    /// <param name="action">The delegate invoked to append a log entry.</param>
    /// <param name="state">The state passed to <paramref name="action"/>.</param>
    void AppendEntry<TState>(Action<TState, IBufferWriter<byte>> action, TState state);

    /// <summary>
    /// Appends an entry to the log for the state machine which holds this instance.
    /// </summary>
    /// <typeparam name="TState">The state, passed to the <paramred name="action"/> delegate.</typeparam>
    /// <param name="action">The delegate invoked to append a log entry.</param>
    /// <param name="state">The state passed to <paramref name="action"/>.</param>
    void AppendEntries<TState>(Action<TState, StateMachineStorageWriter> action, TState state);
}
