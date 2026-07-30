namespace JournaledTodoList.WebApp.Grains;

[GenerateSerializer, Immutable]
public record class TodoItem(int Id, string Title, bool IsCompleted);
