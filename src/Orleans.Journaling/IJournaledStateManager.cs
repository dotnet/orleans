using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling;

/// <summary>
/// Manages the durable states associated with a journal.
/// </summary>
public interface IJournaledStateManager : IAsyncDisposable
{
    /// <inheritdoc/>
    ValueTask IAsyncDisposable.DisposeAsync() => default;

    /// <summary>
    /// Initializes the state manager by replaying its journal.
    /// </summary>
    /// <remarks>
    /// A failed initialization permanently fences this instance. Recover by creating a new manager and new state instances.
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
    /// Registers an observer for state operations, initialization replay, and terminal failure notifications.
    /// </summary>
    /// <param name="observer">The observer.</param>
    /// <remarks>
    /// Observers must be registered before <see cref="InitializeAsync"/> begins.
    /// Each operation uses a stable snapshot of registered observers. The default implementation
    /// throws <see cref="NotSupportedException"/>; implementations supporting observers override this method.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="observer"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The observer is already registered.</exception>
    /// <exception cref="NotSupportedException">
    /// Initialization has started, or the manager uses the default implementation.
    /// </exception>
    void RegisterObserver(IJournaledStateObserver observer) =>
        throw new NotSupportedException("This journaled state manager does not support observers.");

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
    /// Stage mutations only after the operation has established that they are safe to commit. Pending changes
    /// are shared by all callers using this manager. A write failure permanently fences the manager and requests
    /// deactivation of its owning grain. Owners of standalone managers must dispose the failed instance and
    /// create a new manager with new state instances to recover durable state.
    /// Cancellation stops the caller's wait; an already queued write continues to completion.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask WriteStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resets this instance, removing any persistent state.
    /// </summary>
    /// <remarks>
    /// Quiesce other operations before deleting state: deletion resets every registered durable state.
    /// A failed deletion permanently fences the manager and requests deactivation of its owning grain.
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
}
