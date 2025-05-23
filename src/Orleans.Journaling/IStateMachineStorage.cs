namespace Orleans.Journaling;

/// <summary>
/// Provides storage for state machines.
/// </summary>
public interface IStateMachineStorage
{
    /// <summary>
    /// Returns an ordered collection of all log segments belonging to this instance.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An ordered collection of all log segments belonging to this instance.</returns>
    IAsyncEnumerable<LogExtent> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the log with the provided value atomically.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask ReplaceAsync(LogExtentBuilder value, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the provided segment to the log atomically.
    /// </summary>
    /// <param name="value">The segment to append.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    ValueTask AppendAsync(LogExtentBuilder value, CancellationToken cancellationToken);

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
