namespace JournaledTodoList.WebApp.Grains.Events;

[GenerateSerializer, Immutable]
public record class TodoItemToggled(int ItemId, DateTimeOffset Timestamp) : TodoListEvent(Timestamp)
{
    public override string GetDescription() => $"Toggled completion status of item {ItemId}";
}
