using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling;

/// <summary>
/// Manages a grain's durable state, whose named components share a journal and write acknowledgement boundary.
/// </summary>
/// <remarks>
/// Declare state components during grain construction or synchronous activation setup, before initialization.
/// Operations run in the owning activation's execution context. Existing components can be retrieved after
/// initialization, and state contents are available after recovery completes.
/// </remarks>
public interface IDurableStateManager
{
    /// <summary>
    /// Gets an existing state component or creates it using the registered implementation of its application contract.
    /// </summary>
    /// <typeparam name="TState">The application contract of the state.</typeparam>
    /// <param name="name">The stable, ordinal, case-sensitive state name.</param>
    /// <returns>The existing or newly created state instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// The name has an incompatible state type, the contract is not registered, or initialization has begun and the state does not exist.
    /// </exception>
    TState GetOrAddState<TState>(string name) where TState : class;

    /// <summary>
    /// Attempts to retrieve an existing state component without creating it.
    /// </summary>
    /// <typeparam name="TState">The application contract of the state.</typeparam>
    /// <param name="name">The stable, ordinal, case-sensitive state name.</param>
    /// <param name="state">The existing state, if found.</param>
    /// <returns><see langword="true"/> if a compatible state exists; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException">The name has an incompatible state type.</exception>
    bool TryGetState<TState>(string name, [NotNullWhen(true)] out TState? state) where TState : class;

    /// <summary>
    /// Persists pending changes to the shared journal.
    /// </summary>
    /// <remarks>
    /// Stage mutations only after establishing that they are safe to commit. Pending changes are shared
    /// by all interleaved callers using this manager, so a write can include another caller's changes.
    /// Storage acknowledgement establishes durability. A failed journal operation permanently fences the
    /// manager; a new activation recovers the durable state.
    /// Cancellation stops the caller's wait; an already queued write continues to its storage outcome.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel the caller's wait.</param>
    /// <returns>A task representing the write acknowledgement.</returns>
    ValueTask WriteStateAsync(CancellationToken cancellationToken = default);
}
