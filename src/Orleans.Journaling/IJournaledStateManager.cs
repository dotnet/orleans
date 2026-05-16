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
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask WriteStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resets this instance, removing any persistent state.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask DeleteStateAsync(CancellationToken cancellationToken);
}
