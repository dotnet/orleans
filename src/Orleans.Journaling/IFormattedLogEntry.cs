namespace Orleans.Journaling;

/// <summary>
/// A format-owned log entry which can be copied forward without interpreting it.
/// </summary>
public interface IFormattedLogEntry
{
    /// <summary>
    /// Gets the durable operation payload bytes for the formatted entry.
    /// </summary>
    ReadOnlyMemory<byte> Payload { get; }
}

/// <summary>
/// Buffers format-owned log entries for a stream which is not currently registered.
/// </summary>
public interface IFormattedLogEntryBuffer
{
    /// <summary>
    /// Adds a formatted log entry to the buffer.
    /// </summary>
    /// <param name="entry">The formatted log entry.</param>
    void AddFormattedEntry(IFormattedLogEntry entry);

    /// <summary>
    /// Gets the buffered formatted entries.
    /// </summary>
    IReadOnlyList<IFormattedLogEntry> FormattedEntries { get; }
}
