using System.Collections.Immutable;

namespace JournaledTodoList.WebApp.Grains;

public interface ITodoListRegistryGrain : IGrainWithStringKey
{
    Task RegisterTodoListAsync(TodoListReference todoListReference);

    Task<ImmutableArray<TodoListReference>> GetAllTodoListsAsync();

    Task Subscribe(ITodoListRegistryObserver observer);

    Task Unsubscribe(ITodoListRegistryObserver observer);
}
