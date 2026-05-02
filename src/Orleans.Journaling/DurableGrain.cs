using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public abstract class DurableGrain : Grain, IGrainBase
{
    protected DurableGrain()
    {
        StateMachineManager = ServiceProvider.GetRequiredService<IStateMachineManager>();
        if (StateMachineManager is ILifecycleParticipant<IGrainLifecycle> participant)
        {
            participant.Participate(((IGrainBase)this).GrainContext.ObservableLifecycle);
        }
    }

    protected IStateMachineManager StateMachineManager { get; }

    protected TStateMachine GetOrCreateStateMachine<TStateMachine>(string name) where TStateMachine : class, IDurableStateMachine
        => GetOrCreateStateMachine(name, static sp => sp.GetRequiredService<TStateMachine>(), ServiceProvider);

    protected TStateMachine GetOrCreateStateMachine<TState, TStateMachine>(string name, Func<TState, TStateMachine> createStateMachine, TState state) where TStateMachine : class, IDurableStateMachine
    {
        if (StateMachineManager.TryGetStateMachine(name, out var stateMachine))
        {
            return stateMachine as TStateMachine
                ?? throw new InvalidOperationException($"A state machine named '{name}' already exists with an incompatible type {stateMachine.GetType()} versus {typeof(TStateMachine)}");
        }

        var result = createStateMachine(state);
        StateMachineManager.RegisterStateMachine(name, result);
        return result;
    }

    protected ValueTask WriteStateAsync(CancellationToken cancellationToken = default) => StateMachineManager.WriteStateAsync(cancellationToken);
}
