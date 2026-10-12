using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling;

/// <summary>
/// Owns journal recovery and persistence for registered state machines.
/// </summary>
/// <remarks>
/// The owner registers state machines and initializes the journal before using recovered state.
/// State machine instances and their dependencies retain the lifetime assigned by their caller.
/// Disposal drains owned storage and hook operations before releasing resources.
/// </remarks>
public interface IJournaledStateManager : IAsyncDisposable
{
    /// <summary>
    /// Adds a hook before the next persistence operation which follows this registration.
    /// </summary>
    /// <param name="hook">The hook to register.</param>
    /// <param name="index">The insertion index, or <see langword="null"/> to append.</param>
    /// <param name="cancellationToken">The token used to cancel the caller's wait.</param>
    /// <returns>A completion representing application of the registration.</returns>
    /// <remarks>
    /// Registration completes synchronously before initialization begins, supporting constructor registration.
    /// After initialization begins, idle registration applies synchronously; otherwise it is queued
    /// between owned operations, after the current operation's after hooks finish.
    /// Before hooks execute in reverse registration order; after hooks execute in forward order.
    /// Register the final before hook at index zero. Writes then prepare the registered states before capture.
    /// Registration is independent of state-machine registration and persists through recovery and deletion.
    /// Every implementation provides registration and invokes hooks at the documented operation boundaries.
    /// Queued registration is a write-coalescing barrier.
    /// Cancellation ends the caller's wait while admitted registration remains owned by the manager.
    /// Synchronous state callbacks can request registration and observe its completion outside the callback.
    /// Await registration before staging changes whose safety depends on the added hook.
    /// </remarks>
    ValueTask AddHookAsync(IJournaledStateHook hook, int? index = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a hook between persistence operations.
    /// </summary>
    /// <param name="hook">The registered hook to remove.</param>
    /// <param name="cancellationToken">The token used to cancel the caller's wait.</param>
    /// <returns>A completion representing application of the removal.</returns>
    /// <exception cref="InvalidOperationException">The hook is not registered when the removal is applied.</exception>
    /// <remarks>
    /// Removal has the same ownership and ordering guarantees as <see cref="AddHookAsync"/>.
    /// </remarks>
    ValueTask RemoveHookAsync(IJournaledStateHook hook, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    ValueTask IAsyncDisposable.DisposeAsync() => default;

    /// <summary>
    /// Initializes the state manager by replaying its journal.
    /// </summary>
    /// <remarks>
    /// A recovery failure fails the current initialization attempt and leaves this instance uninitialized.
    /// A subsequent call retries recovery from the beginning, resetting and replaying the registered state machines.
    /// Writes become available after initialization succeeds. A manager fenced by a persistence failure requires a new instance.
    /// Owner shutdown which cancels recovery cancels initialization and leaves this instance stopped.
    /// Cancelling the caller's token ends only that caller's wait while owned recovery continues.
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
    /// The manager invokes each state's <see cref="IStateMachine.PrepareWriteAsync"/> after before hooks,
    /// completing a synchronous ready pass before capture.
    /// A storage or state-processing failure fences the manager; recovery requires a new manager and state machine instances.
    /// Cancellation stops the caller's wait; an already queued write continues to its storage outcome.
    /// State preparation or before-hook failure reports <see cref="JournaledStatePreCommitException"/> and retains pending
    /// changes for an explicit retry. After-hook failure reports
    /// <see cref="JournaledStatePostCommitException"/> after successful persistence.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel the caller's wait.</param>
    /// <returns>A task representing the write acknowledgement.</returns>
    ValueTask WriteStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets this instance, removing any persistent state.
    /// </summary>
    /// <remarks>
    /// The caller keeps other operations quiescent through completion: deletion resets every registered state machine.
    /// Cancellation ends the caller's wait; an already queued deletion continues to its storage and reset outcome.
    /// A storage or state-reset failure permanently fences the manager and requests deactivation of its owning grain.
    /// Before-hook failure reports <see cref="JournaledStatePreCommitException"/> with state retained.
    /// After-hook failure reports <see cref="JournaledStatePostCommitException"/> after storage deletion
    /// and state reset succeed.
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
