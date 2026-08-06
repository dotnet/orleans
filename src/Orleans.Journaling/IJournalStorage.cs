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
    /// pass <see langword="null"/> or <see cref="JournalMetadata.Empty"/>. Metadata passed during one read must have the same
    /// <see cref="IJournalMetadata.Format"/> value for every call.
    /// </remarks>
    /// <param name="consumer">The consumer of ordered raw journal data. Chunk boundaries are not journal-entry boundaries.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken);

    /// <summary>
    /// Creates this journal storage instance if it does not already exist.
    /// </summary>
    /// <remarks>
    /// Initial metadata is only applied when the storage instance is created. If the journal was
    /// already created by a write, this method returns <see langword="false"/> and does not update metadata.
    /// </remarks>
    /// <param name="metadata">Initial caller-owned metadata properties.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if storage was created; otherwise, <see langword="false"/>.</returns>
    ValueTask<bool> CreateIfNotExistsAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{nameof(IJournalStorage)} implementation does not support journal storage metadata operations.");

    /// <summary>
    /// Gets metadata for this journal storage instance.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The metadata, or <see langword="null"/> if the storage instance does not exist.</returns>
    ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{nameof(IJournalStorage)} implementation does not support journal storage metadata operations.");

    /// <summary>
    /// Conditionally updates caller-owned metadata properties.
    /// </summary>
    /// <remarks>
    /// Implementations apply updates atomically against the current metadata. When
    /// <paramref name="expectedETag"/> is not <see langword="null"/>, providers which support ETags
    /// must only apply the update if the current metadata ETag matches it. Provider-owned metadata
    /// must be preserved.
    /// </remarks>
    /// <param name="set">Metadata properties to set.</param>
    /// <param name="remove">Metadata properties to remove.</param>
    /// <param name="expectedETag">The expected metadata ETag, or <see langword="null"/> for an unconditional update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current metadata if the update was applied or made no changes; otherwise, <see langword="null"/>.</returns>
    ValueTask<IJournalMetadata?> UpdateMetadataAsync(
        IReadOnlyDictionary<string, string>? set = null,
        IEnumerable<string>? remove = null,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{nameof(IJournalStorage)} implementation does not support journal storage metadata operations.");

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
    IJournalStorage CreateStorage(JournalId journalId);
}
