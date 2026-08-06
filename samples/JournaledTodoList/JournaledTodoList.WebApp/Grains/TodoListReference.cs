namespace JournaledTodoList.WebApp.Grains;

[GenerateSerializer, Immutable]
public record class TodoListReference(string Id, string Name);
