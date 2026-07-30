using System.Collections.Immutable;

namespace JournaledTodoList.WebApp.Grains;

[GenerateSerializer, Immutable]
public record class TodoList(
    string Name,
    ImmutableArray<TodoItem> Items,
    DateTimeOffset Timestamp);
