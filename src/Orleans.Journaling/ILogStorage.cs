using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides log storage.
/// </summary>
public interface ILogStorage
{
    /// <summary>
    /// Reads all log data belonging to this instance into <paramref name="buffer"/> and invokes <paramref name="consume"/> after adding data.
    /// </summary>
    /// <param name="buffer">The buffer to append ordered raw log data to. Chunk boundaries are not log-entry boundaries.</param>
    /// <param name="consume">Callback invoked after appending data so the caller can consume complete entries.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the log with the provided value atomically.
    /// </summary>
    /// <param name="value">The encoded log bytes to write. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the provided segment to the log atomically.
    /// </summary>
    /// <param name="value">The encoded log bytes to append. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the log atomically.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask DeleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a value indicating whether compaction has been requested.
    /// </summary>
    bool IsCompactionRequested { get; }
}
