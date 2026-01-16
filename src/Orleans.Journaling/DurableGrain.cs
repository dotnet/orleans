using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Messaging;

namespace Orleans.Journaling;

public abstract class DurableGrain : Grain, IGrainBase
{
    private IDurableOutbox? _outbox;

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

    /// <summary>
    /// Gets the outbox for lazy initialization of delivery triggering.
    /// </summary>
    private IDurableOutbox? GetOutboxForDelivery()
    {
        // Lazy initialization - only get the outbox if it hasn't been retrieved yet
        // This avoids always pulling it from DI when it might not be used
        return _outbox ??= ServiceProvider.GetService<IDurableOutbox>();
    }

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
    /// Writes state and triggers delivery of any pending outbox messages.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the state is written and delivery is triggered.</returns>
    protected async ValueTask WriteStateAsync(CancellationToken cancellationToken = default)
    {
        // First, persist all state changes (including outbox messages)
        await StateManager.WriteStateAsync(cancellationToken).ConfigureAwait(false);

        // Then trigger delivery of any pending outbox messages
        var outbox = GetOutboxForDelivery();
        if (outbox is not null && outbox.Count > 0)
        {
            await outbox.DeliverPendingMessagesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
