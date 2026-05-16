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
    /// <remarks>
    /// Implementations should throw <see cref="Orleans.Storage.InconsistentStateException"/> when optimistic concurrency fails.
    /// </remarks>
    /// <param name="value">The encoded journal bytes to write. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the provided segment to the journal atomically.
    /// </summary>
    /// <remarks>
    /// Implementations should throw <see cref="Orleans.Storage.InconsistentStateException"/> when optimistic concurrency fails.
    /// </remarks>
    /// <param name="value">The encoded journal bytes to append. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the journal atomically.
    /// </summary>
    /// <remarks>
    /// Implementations should throw <see cref="Orleans.Storage.InconsistentStateException"/> when optimistic concurrency fails.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask DeleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a value indicating whether compaction has been requested.
    /// </summary>
    bool IsCompactionRequested { get; }
}

/// <summary>
/// Creates journal storage.
/// </summary>
public interface IJournalStorageProvider
{
    /// <summary>
    /// Creates journal storage for the provided journal id.
    /// </summary>
    /// <param name="journalId">The journal id.</param>
    /// <returns>The journal storage instance.</returns>
    IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        throw new NotSupportedException(
            $"This journal storage provider does not support grain-independent journals. Implement {nameof(CreateStorage)}({nameof(JournalId)}) to support on-demand journals.");
    }

    /// <summary>
    /// Creates journal storage for the provided grain context.
    /// </summary>
    /// <param name="grainContext">The grain context.</param>
    /// <returns>The journal storage instance.</returns>
    IJournalStorage CreateStorage(IGrainContext grainContext)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        return CreateStorage(JournalId.FromGrainId(grainContext.GrainId));
    }
}
