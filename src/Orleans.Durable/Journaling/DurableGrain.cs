using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Provides convenience methods for grains which manage journaled durable state.
/// </summary>
public abstract class DurableGrain : Grain, IGrainBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableGrain"/> class and resolves its state manager.
    /// </summary>
    /// <remarks>
    /// The standard state manager enrolls in the grain lifecycle during grain-bound construction.
    /// </remarks>
    protected DurableGrain()
    {
        StateManager = ServiceProvider.GetRequiredService<IDurableStateManager>();
    }

    /// <summary>
    /// Gets the journaled state manager for this grain activation.
    /// </summary>
    protected IDurableStateManager StateManager { get; }

    /// <summary>
    /// Writes the registered journaled state.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the write operation.</returns>
    protected ValueTask WriteStateAsync(CancellationToken cancellationToken = default) => StateManager.WriteStateAsync(cancellationToken);
}
