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
        StateManager = ServiceProvider.GetRequiredService<IJournaledStateManager>();
    }

    /// <summary>
    /// Gets the journaled state manager for this grain activation.
    /// </summary>
    protected IJournaledStateManager StateManager { get; }

    /// <summary>
    /// Gets the registered state with the specified name or creates it using the grain service provider.
    /// </summary>
    /// <typeparam name="TState">The type of journaled state.</typeparam>
    /// <param name="name">The name used to register the state.</param>
    /// <returns>The existing or newly created state.</returns>
    protected TState GetOrCreateState<TState>(string name) where TState : class, IJournaledState
        => GetOrCreateState(name, static sp => sp.GetRequiredService<TState>(), ServiceProvider);

    /// <summary>
    /// Gets the registered state with the specified name or creates it using the supplied factory and argument.
    /// </summary>
    /// <typeparam name="TArg">The type of argument passed to the state factory.</typeparam>
    /// <typeparam name="TState">The type of journaled state.</typeparam>
    /// <param name="name">The name used to register the state.</param>
    /// <param name="createState">The factory used to create the state when it is not already registered.</param>
    /// <param name="arg">The argument passed to <paramref name="createState"/>.</param>
    /// <returns>The existing or newly created state.</returns>
    protected TState GetOrCreateState<TArg, TState>(string name, Func<TArg, TState> createState, TArg arg) where TState : class, IJournaledState
    {
        if (StateManager.TryGetState(name, out var state))
        {
            return state as TState
                ?? throw new InvalidOperationException($"A state named '{name}' already exists with an incompatible type {state.GetType()} versus {typeof(TState)}");
        }

        var result = createState(arg);
        StateManager.RegisterState(name, result);
        return result;
    }

    /// <summary>
    /// Writes the registered journaled state.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the write operation.</returns>
    protected ValueTask WriteStateAsync(CancellationToken cancellationToken = default) => StateManager.WriteStateAsync(cancellationToken);
}
