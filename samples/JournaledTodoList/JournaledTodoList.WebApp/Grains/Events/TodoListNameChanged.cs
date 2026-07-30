namespace JournaledTodoList.WebApp.Grains.Events;

[GenerateSerializer, Immutable]
public record class TodoListNameChanged(string Name, DateTimeOffset Timestamp) : TodoListEvent(Timestamp)
{
    public override string GetDescription() => $"Todo list name changed to {Name}";
}
