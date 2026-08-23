using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling;

/// <summary>
/// Manages the states for a given grain.
/// </summary>
public interface IJournaledStateManager : IAsyncDisposable
{
    /// <inheritdoc/>
    ValueTask IAsyncDisposable.DisposeAsync() => default;

    /// <summary>
    /// Initializes the state manager.
    /// </summary>
    /// <remarks>
    /// Cancellation is observed before initialization is queued. Once recovery begins, the operation completes
    /// before returning so callers never observe state while recovery is still mutating it.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Registers a state with the manager.
    /// </summary>
    /// <param name="name">The state's stable identifier.</param>
    /// <param name="state">The state instance to register.</param>
    void RegisterState(string name, IJournaledState state);

    /// <summary>
    /// Attempts to get a state registered with the manager.
    /// </summary>
    /// <param name="name">The state's stable identifier.</param>
    /// <param name="state">The state instance, if one is registered for <paramref name="name"/>.</param>
    bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state);

    /// <summary>
    /// Prepares and persists an update to the journal.
    /// </summary>
    /// <remarks>
    /// When the operation writes a snapshot, the complete captured state is replaced atomically. If storage
    /// fails without reporting an optimistic-concurrency conflict, the captured changes remain pending and can be retried.
    /// Changes made while storage is awaiting are not consumed by the completed operation.
    /// An optimistic-concurrency conflict recovers the winning journal generation and discards the losing in-memory changes.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask WriteStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Discards uncommitted mutations and reloads the last durable state.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resets this instance, removing any persistent state.
    /// </summary>
    /// <remarks>
    /// Cancellation is observed before deletion is queued. Once deletion begins, the operation completes before returning.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask DeleteStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets an approximate count of bytes accumulated in the in-memory journal buffer that have
    /// not yet been flushed to storage. Returns a negative value when the implementation does not
    /// support sampling pending bytes.
    /// </summary>
    /// <remarks>
    /// This is intended for diagnostics and instrumentation; the returned value may race with
    /// concurrent writers and should not be used for correctness decisions.
    /// </remarks>
    long PendingWriteByteCount => -1;

    /// <summary>
    /// Gets a value indicating whether any registered state has changes which have not been written to storage.
    /// </summary>
    bool HasPendingWrites => PendingWriteByteCount != 0;
}
