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
    /// Reads as many complete log entries as possible from <paramref name="input"/> and applies them to resolved state machines.
    /// </summary>
    /// <param name="input">The buffered persisted log data, including its completion state.</param>
    /// <param name="resolver">The resolver used to locate state machines by log stream id.</param>
    /// <remarks>
    /// If <see cref="LogReadBuffer.IsCompleted"/> is <see langword="false"/>, this method returns when
    /// there is insufficient data to read another complete log entry. If <see cref="LogReadBuffer.IsCompleted"/>
    /// is <see langword="true"/>, this method throws if the remaining data does not contain complete log entries.
    /// </remarks>
    void Read(LogReadBuffer input, IStateMachineResolver resolver);
}
