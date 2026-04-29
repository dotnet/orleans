using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides storage for state machines.
/// </summary>
public interface IStateMachineStorage
{
    /// <summary>
    /// Gets the configured physical log format key for this storage instance.
    /// </summary>
    string LogFormatKey { get; }

    /// <summary>
    /// Reads all log data belonging to this instance and pushes it to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The consumer which receives raw log data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReadAsync(IStateMachineLogDataConsumer consumer, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the log with the provided value atomically.
    /// </summary>
    /// <param name="value">The encoded log bytes to write. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReplaceAsync(ArcBuffer value, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the provided segment to the log atomically.
    /// </summary>
    /// <param name="value">The encoded log bytes to append. The storage provider must not retain this buffer after the returned task completes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask AppendAsync(ArcBuffer value, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the state machine's log atomically.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask DeleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a value indicating whether the state machine has requested a snapshot.
    /// </summary>
    bool IsCompactionRequested { get; }
}
