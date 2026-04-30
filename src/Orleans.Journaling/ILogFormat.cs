using Orleans.Serialization.Buffers;

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
    /// Creates a writer for a new mutable log segment.
    /// </summary>
    /// <returns>A new log segment writer.</returns>
    ILogSegmentWriter CreateWriter();

    /// <summary>
    /// Attempts to read one complete log entry from <paramref name="input"/> and push the decoded entry to <paramref name="sink"/>.
    /// </summary>
    /// <param name="input">The buffered persisted log data.</param>
    /// <param name="sink">The sink which receives decoded log entries.</param>
    /// <param name="isCompleted">A value indicating whether no more persisted bytes will be supplied after <paramref name="input"/>.</param>
    /// <returns><see langword="true"/> if a complete entry was consumed; otherwise, <see langword="false"/>.</returns>
    bool TryRead(ArcBufferReader input, ILogEntrySink sink, bool isCompleted);
}
