using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling;

/// <summary>
/// Owns journal recovery and persistence for registered state machines.
/// </summary>
/// <remarks>
/// The owner registers state machines and initializes the journal before using recovered state.
/// State machine instances and their dependencies retain the lifetime assigned by their caller.
/// Disposing this manager stops journal processing and releases its journal resources.
/// </remarks>
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
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a state machine before initialization begins.
    /// </summary>
    /// <param name="name">The state machine's stable identifier.</param>
    /// <param name="stateMachine">The state machine instance to register.</param>
    /// <exception cref="InvalidOperationException">Initialization has begun or the name is already registered.</exception>
    void RegisterStateMachine(string name, IStateMachine stateMachine);

    /// <summary>
    /// Attempts to retrieve a registered state machine.
    /// </summary>
    /// <param name="name">The state machine's stable identifier.</param>
    /// <param name="stateMachine">The registered state machine, if found.</param>
    /// <returns><see langword="true"/> if the state machine is registered; otherwise, <see langword="false"/>.</returns>
    bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? stateMachine);

    /// <summary>
    /// Persists pending changes from the registered state machines to the journal.
    /// </summary>
    /// <remarks>
    /// Stage mutations only after establishing that they are safe to commit. Pending changes are shared
    /// by all callers using this manager. Storage acknowledgement establishes durability.
    /// A failed journal operation fences the manager; recovery requires a new manager and state machine instances.
    /// Cancellation stops the caller's wait; an already queued write continues to its storage outcome.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel the caller's wait.</param>
    /// <returns>A task representing the write acknowledgement.</returns>
    ValueTask WriteStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets this instance, removing any persistent state.
    /// </summary>
    /// <remarks>
    /// Quiesce other operations before deleting state: deletion resets every registered state machine.
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
