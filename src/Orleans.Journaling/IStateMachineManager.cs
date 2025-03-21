using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling;

/// <summary>
/// Manages the state machines for a given grain.
/// </summary>
public interface IStateMachineManager
{
    /// <summary>
    /// Initializes the state machine manager.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> which represents the operation.</returns>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Registers a state machine with the manager.
    /// </summary>
    /// <param name="name">The state machine's stable identifier.</param>
    /// <param name="stateMachine">The state machine instance to register.</param>
    void RegisterStateMachine(string name, IDurableStateMachine stateMachine);

    /// <summary>
    /// Registers a state machine with the manager.
    /// </summary>
    /// <param name="name">The state machine's stable identifier.</param>
    /// <param name="stateMachine">The state machine instance to register.</param>
    bool TryGetStateMachine(string name, [NotNullWhen(true)] out IDurableStateMachine? stateMachine);

    /// <summary>
    /// Prepares and persists an update to the log.
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
