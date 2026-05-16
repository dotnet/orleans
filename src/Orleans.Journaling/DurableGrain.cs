using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public abstract class DurableGrain : Grain, IGrainBase
{
    protected DurableGrain()
    {
        StateManager = ServiceProvider.GetRequiredService<IJournaledStateManager>();
        if (StateManager is ILifecycleParticipant<IGrainLifecycle> participant)
        {
            participant.Participate(((IGrainBase)this).GrainContext.ObservableLifecycle);
        }
    }

    protected IJournaledStateManager StateManager { get; }

    protected TState GetOrCreateState<TState>(string name) where TState : class, IJournaledState
        => GetOrCreateState(name, static sp => sp.GetRequiredService<TState>(), ServiceProvider);

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

    protected ValueTask WriteStateAsync(CancellationToken cancellationToken = default) => StateManager.WriteStateAsync(cancellationToken);
}
