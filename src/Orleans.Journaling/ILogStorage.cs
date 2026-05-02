using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides log storage.
/// </summary>
public interface ILogStorage
{
    /// <summary>
    /// Reads all log data belonging to this instance and sends it to <paramref name="consumer"/>.
    /// </summary>
    /// <remarks>
    /// Implementations must notify <paramref name="consumer"/> when the read is complete by passing a
    /// <see cref="LogReadBuffer"/> with <see cref="LogReadBuffer.IsCompleted"/> set to <see langword="true"/>.
    /// </remarks>
    /// <param name="consumer">The consumer of ordered raw log data. Chunk boundaries are not log-entry boundaries.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReadAsync(ILogStorageConsumer consumer, CancellationToken cancellationToken);

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
