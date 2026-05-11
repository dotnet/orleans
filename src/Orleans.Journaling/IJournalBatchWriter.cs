using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Writes a mutable batch of physical state journal entries.
/// </summary>
/// <remarks>
/// The batch writer is caller-owned and may be reused after a successful append or replace
/// by calling <see cref="Reset"/>.
/// </remarks>
public interface IJournalBatchWriter : IDisposable
{
    /// <summary>
    /// Gets the number of bytes currently buffered by this writer, including any active uncommitted entry payload.
    /// This value may exclude format framing that is materialized only when an entry is committed, so it is suitable
    /// as a heuristic but not as an exact persisted byte count. Use <see cref="GetCommittedBuffer"/> after all entries
    /// have been committed to obtain the exact committed byte count.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Creates a writer for entries belonging to <paramref name="streamId"/>.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <returns>A journal stream writer for the specified state.</returns>
    JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId);

    /// <summary>
    /// Gets a borrowed buffer containing only committed bytes which are safe to persist.
    /// </summary>
    /// <returns>An <see cref="ArcBuffer"/> containing the committed journal bytes. The caller owns and must dispose the returned buffer.</returns>
    /// <exception cref="InvalidOperationException">An entry is currently active and has not been committed or aborted.</exception>
    ArcBuffer GetCommittedBuffer();

    /// <summary>
    /// Clears all buffered data so the writer can be reused for another batch.
    /// </summary>
    void Reset();
}
