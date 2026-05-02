using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Writes a mutable batch of physical state machine log entries.
/// </summary>
/// <remarks>
/// The batch writer is caller-owned and may be reused after a successful append or replace
/// by calling <see cref="Reset"/>.
/// </remarks>
public interface ILogBatchWriter : IDisposable
{
    /// <summary>
    /// Gets the number of raw bytes written, including any currently active uncommitted entry.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Creates a writer for entries belonging to <paramref name="streamId"/>.
    /// </summary>
    /// <param name="streamId">The durable state machine id.</param>
    /// <returns>A log stream writer for the specified state machine.</returns>
    LogStreamWriter CreateLogStreamWriter(LogStreamId streamId);

    /// <summary>
    /// Gets a borrowed buffer containing only committed bytes which are safe to persist.
    /// </summary>
    /// <returns>An <see cref="ArcBuffer"/> containing the committed log bytes. The caller owns and must dispose the returned buffer.</returns>
    /// <exception cref="InvalidOperationException">An entry is currently active and has not been committed or aborted.</exception>
    ArcBuffer GetCommittedBuffer();

    /// <summary>
    /// Clears all buffered data so the writer can be reused for another batch.
    /// </summary>
    void Reset();
}
