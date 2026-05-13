using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides journal storage.
/// </summary>
public interface IJournalStorage
{
    /// <summary>
    /// Reads all journal data belonging to this instance and sends it to <paramref name="consumer"/>.
    /// </summary>
    /// <remarks>
    /// Implementations must notify <paramref name="consumer"/> when the read is complete by passing a
    /// <see cref="JournalBufferReader"/> with <see cref="JournalBufferReader.IsCompleted"/> set to <see langword="true"/>.
    /// Each call must pass metadata describing the journal file being read. If storage has no metadata,
    /// pass <see langword="null"/> or <see cref="JournalFileMetadata.Empty"/>. Metadata passed during one read must have the same
    /// <see cref="IJournalFileMetadata.Format"/> value for every call.
    /// </remarks>
    /// <param name="consumer">The consumer of ordered raw journal data. Chunk boundaries are not journal-entry boundaries.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the journal with the provided value atomically.
    /// </summary>
    /// <param name="value">The encoded journal bytes to write. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the provided segment to the journal atomically.
    /// </summary>
    /// <param name="value">The encoded journal bytes to append. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the journal atomically.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask DeleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a value indicating whether compaction has been requested.
    /// </summary>
    bool IsCompactionRequested { get; }
}
