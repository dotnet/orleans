using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

internal sealed class DurableStateManager(
    JournaledStateManagerShared shared,
    IJournalStorageProvider storageProvider,
    IGrainContext grainContext)
    : JournaledStateManager(shared, storageProvider, grainContext), IDurableStateManager
{
    public TState GetOrAddState<TState>(string name) where TState : class
    {
        if (TryGetState<TState>(name, out var existing))
        {
            return existing;
        }

        EnsureRegistrationAllowed();
        var result = ServiceProvider.GetKeyedService<TState>(name)
            ?? throw new InvalidOperationException(
                $"No durable state implementation is registered for contract '{typeof(TState)}' and name '{name}'. " +
                "Register the contract using AddStateMachine<TState, TImplementation>.");
        if (result is not IStateMachine stateMachine)
        {
            throw new InvalidOperationException(
                $"The durable state implementation for contract '{typeof(TState)}' and name '{name}' does not implement {nameof(IStateMachine)}.");
        }

        return RegisterResolvedState(name, result, stateMachine);
    }

    public bool TryGetState<TState>(string name, [NotNullWhen(true)] out TState? state) where TState : class
    {
        if (TryGetStateMachine(name, out var stateMachine))
        {
            state = stateMachine as TState
                ?? throw new InvalidOperationException(
                    $"A state named '{name}' is already registered with type '{stateMachine.GetType()}', which is incompatible with '{typeof(TState)}'.");
            return true;
        }

        state = null;
        return false;
    }

    internal TState GetOrAddState<TState, TImplementation>(string name, Func<IServiceProvider, string, TImplementation> factory)
        where TState : class
        where TImplementation : class, TState, IStateMachine
    {
        if (TryGetState<TState>(name, out var existing))
        {
            return existing;
        }

        EnsureRegistrationAllowed();
        var state = factory(ServiceProvider, name)
            ?? throw new InvalidOperationException($"The durable state factory for '{typeof(TState)}' returned null for name '{name}'.");
        return RegisterResolvedState<TState>(name, state, state);
    }

    private TState RegisterResolvedState<TState>(string name, TState state, IStateMachine stateMachine) where TState : class
    {
        if (TryGetState<TState>(name, out var existing))
        {
            if (!ReferenceEquals(existing, state))
            {
                throw new InvalidOperationException($"A different state instance is already registered with name '{name}'.");
            }

            return existing;
        }

        RegisterStateMachine(name, stateMachine);
        return state;
    }
}
