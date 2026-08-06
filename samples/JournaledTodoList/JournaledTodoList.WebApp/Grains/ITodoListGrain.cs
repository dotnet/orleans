using System.Collections.Immutable;
using JournaledTodoList.WebApp.Grains.Events;

namespace JournaledTodoList.WebApp.Grains;

public interface ITodoListGrain : IGrainWithStringKey
{
    Task<TodoList> GetTodoListAsync();

    Task<TodoList?> GetTodoListAtTimestampAsync(DateTimeOffset timestamp);

    Task AddTodoItemAsync(string title);

    Task UpdateTodoItemAsync(int id, string title);

    Task ToggleTodoItemAsync(int id);

    Task RemoveTodoItemAsync(int id);

    Task SetNameAsync(string listName);

    Task<ImmutableArray<TodoListEvent>> GetHistoryAsync();
}
