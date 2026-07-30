using System.Collections.Immutable;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Utilities;

namespace JournaledTodoList.WebApp.Grains;

/// <summary>
/// A "state" based Journaled Grain. It does not save the events, just the state.
/// </summary>
[LogConsistencyProvider(ProviderName = Constants.StateStorageProviderName)]
public sealed class TodoListRegistryGrain(ILogger<TodoListRegistryGrain> logger)
    : JournaledGrain<TodoListRegistryGrain.TodoListRegistry, TodoListReference>
    , ITodoListRegistryGrain
{
    private readonly ObserverManager<ITodoListRegistryObserver> observers = new(
        TimeSpan.FromMinutes(5),
        logger);

    public async Task RegisterTodoListAsync(TodoListReference todoListReference)
    {
        if (State.TodoLists.Contains(todoListReference))
        {
            return;
        }

        RaiseEvent(todoListReference);
        await ConfirmEvents();
        await NotifyObservers();
    }

    // Instead of having Apply methods in TodoListRegistry, we can override
    // the TransitionState method and update the state here.
    protected override void TransitionState(TodoListRegistry state, TodoListReference @event)
    {
        // If there is an existing item with the same Id, replace it with the new item,
        // ensuring the order of the items are kept.
        var existingList = state.TodoLists.FirstOrDefault(x => x.Id == @event.Id);

        if (existingList is not null)
        {
            state.TodoLists = state.TodoLists.Replace(existingList, @event);
        }
        else
        {
            state.TodoLists = state.TodoLists.Add(@event);
        }
    }

    public Task<ImmutableArray<TodoListReference>> GetAllTodoListsAsync()
    {
        return Task.FromResult(State.TodoLists);
    }

    public Task Subscribe(ITodoListRegistryObserver observer)
    {
        observers.Subscribe(observer, observer);
        observer.OnTodoListsChanged(State.TodoLists);
        return Task.CompletedTask;
    }

    public Task Unsubscribe(ITodoListRegistryObserver observer)
    {
        observers.Unsubscribe(observer);
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        observers.Clear();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    private Task NotifyObservers()
        => observers.Notify(observer => observer.OnTodoListsChanged(State.TodoLists));

    [GenerateSerializer, Immutable]
    public sealed class TodoListRegistry
    {
        [Id(0)]
        public ImmutableArray<TodoListReference> TodoLists { get; set; } = [];
    }
}
