namespace Orleans.Journaling;

/// <summary>
/// Manages the durable states associated with a journal.
/// </summary>
public interface IJournaledStateManager : IDurableStateManager, IAsyncDisposable
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
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a state machine before initialization begins.
    /// </summary>
    /// <param name="name">The state's stable identifier.</param>
    /// <param name="stateMachine">The state machine instance to register.</param>
    /// <exception cref="InvalidOperationException">Initialization has begun or the name is already registered.</exception>
    void RegisterStateMachine(string name, IStateMachine stateMachine);

    /// <summary>
    /// Resets this instance, removing any persistent state.
    /// </summary>
    /// <remarks>
    /// Quiesce other operations before deleting state: deletion resets every registered durable state.
    /// A failed deletion permanently fences the manager and requests deactivation of its owning grain.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask DeleteStateAsync(CancellationToken cancellationToken = default);

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
