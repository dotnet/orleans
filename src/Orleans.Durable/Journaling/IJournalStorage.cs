using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides journal storage.
/// </summary>
/// <remarks>
/// <para>
/// A journal is an ordered sequence of segments. Each successful <see cref="AppendAsync"/> adds one segment to the end,
/// and each successful <see cref="ReplaceAsync"/> replaces the whole journal with one segment. The journal is the source
/// of truth for journaled state, so every implementation provides the following guarantees.
/// </para>
/// <para>
/// Callers await each operation before starting another operation through the same storage instance. Separate instances
/// coordinate through the storage provider's write preconditions.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>All-or-nothing writes.</b> <see cref="AppendAsync"/> and <see cref="ReplaceAsync"/> make the whole segment visible
/// at once or none of it. Successful reads observe whole segments after a process or storage crash, a timeout, a
/// precondition conflict, or a retry inside the provider. A segment which the provider stores across several records or
/// requests becomes visible through one atomic publication step.
/// </description></item>
/// <item><description>
/// <b>Limits fail the whole call.</b> A segment which exceeds a provider limit, such as a maximum request, block,
/// transaction, or value size, fails the call with an exception, and nothing of it becomes visible.
/// </description></item>
/// <item><description>
/// <b>Failure outcomes.</b> A definitively rejected write, such as a locally detected limit or invalid input, preserves
/// the earlier content. After a request has an indeterminate outcome, such as a timeout or a lost response, the segment was
/// written whole or not at all. A retry can report a precondition conflict after the original request committed, so recovery
/// determines the outcome even when the final exception is <see cref="Orleans.Storage.InconsistentStateException"/>.
/// After any failed write, the journaled state manager fences itself and recovers its state from a fresh read.
/// </description></item>
/// <item><description>
/// <b>Whole, ordered reads.</b> A successful <see cref="ReadAsync"/> returns the content of one version of the journal:
/// every segment written up to that version, whole and in write order, and nothing of any later write. Callers discard
/// data delivered by a failed read and recover from the beginning.
/// </description></item>
/// <item><description>
/// <b>Write precondition.</b> Each storage instance holds a write precondition established by its first successful
/// <see cref="ReadAsync"/> or creation, including an observed absence, and advanced by its own writes. <see cref="AppendAsync"/>,
/// <see cref="ReplaceAsync"/>, and <see cref="DeleteAsync"/> succeed only if the journal is still at that version, so a
/// write after observing an absent journal requires the journal to still be absent. Otherwise they throw
/// <see cref="Orleans.Storage.InconsistentStateException"/>; each conflicting request leaves storage unchanged. A successful write, including creating
/// the journal with <see cref="CreateIfNotExistsAsync"/>, advances the precondition to the version it produced. Callers read
/// the journal, or create it, before writing through an instance: a write through an instance which has done neither throws
/// <see cref="InvalidOperationException"/>. Metadata operations never establish or move the precondition, and metadata-only
/// updates never change the journal's content.
/// </description></item>
/// </list>
/// <para>
/// Subsequent reads require the journal to match the instance's established precondition and throw
/// <see cref="Orleans.Storage.InconsistentStateException"/> on a content change. A caller can therefore reread its own
/// journal for history or compaction while preserving its write precondition. Recovery after a conflict uses a fresh
/// storage instance.
/// </para>
/// </remarks>
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
    /// <see cref="IJournalMetadata.FormatKey"/> value for every call.
    /// The data is the whole content of one version of the journal, with segments in write order. The version read, including
    /// an absent journal, establishes an unread instance's write precondition. Subsequent reads validate that precondition
    /// and throw <see cref="Orleans.Storage.InconsistentStateException"/> if another writer changed the content.
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
    /// Creating the journal makes the empty journal this instance's write precondition; finding an existing journal leaves the
    /// precondition unchanged.
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
    /// The whole value becomes the journal's only segment, or the journal keeps its earlier content. A value which exceeds a
    /// provider limit fails the call. Implementations throw <see cref="Orleans.Storage.InconsistentStateException"/> when the
    /// journal is no longer at this instance's write precondition, and <see cref="InvalidOperationException"/> when this
    /// instance has neither read nor created the journal.
    /// </remarks>
    /// <param name="value">The encoded journal bytes to write. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the provided segment to the journal atomically.
    /// </summary>
    /// <remarks>
    /// The whole segment becomes visible after every earlier segment, or none of it does. A segment which exceeds a provider
    /// limit fails the call. Implementations throw <see cref="Orleans.Storage.InconsistentStateException"/> when the journal
    /// is no longer at this instance's write precondition, and <see cref="InvalidOperationException"/> when this instance has
    /// neither read nor created the journal. The segment must contain at least one byte.
    /// </remarks>
    /// <param name="value">The encoded journal bytes to append. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The segment is empty.</exception>
    ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the journal atomically.
    /// </summary>
    /// <remarks>
    /// Implementations throw <see cref="Orleans.Storage.InconsistentStateException"/> when the journal is no longer at this
    /// instance's write precondition, and <see cref="InvalidOperationException"/> when this instance has neither read nor
    /// created the journal. After a successful delete, the precondition is an absent journal.
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
