using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Consumes decoded log entries.
/// </summary>
public interface ILogEntrySink
{
    /// <summary>
    /// Applies one decoded log entry.
    /// </summary>
    /// <param name="streamId">The log stream id.</param>
    /// <param name="payload">The encoded durable operation payload.</param>
    void OnEntry(LogStreamId streamId, ReadOnlySequence<byte> payload);
}
