namespace Orleans.Journaling;

/// <summary>
/// Reads and writes the physical byte format used to persist state machine log entries.
/// </summary>
/// <remarks>
/// A log format owns physical framing for entries. Durable state machine codecs write only
/// the payload for a single entry.
/// </remarks>
public interface ILogFormat
{
    /// <summary>
    /// Creates a writer for a new mutable log batch.
    /// </summary>
    /// <returns>A new log batch writer.</returns>
    ILogBatchWriter CreateWriter();

    /// <summary>
    /// Attempts to read one complete log entry from <paramref name="input"/> and apply it to a resolved state machine.
    /// </summary>
    /// <param name="input">The buffered persisted log data, including its completion state.</param>
    /// <param name="resolver">The resolver used to locate state machines by log stream id.</param>
    /// <returns><see langword="true"/> if a complete entry was consumed; otherwise, <see langword="false"/>.</returns>
    bool TryRead(LogReadBuffer input, IStateMachineResolver resolver);
}
