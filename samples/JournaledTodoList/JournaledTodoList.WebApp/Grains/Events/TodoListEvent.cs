namespace JournaledTodoList.WebApp.Grains.Events;

[GenerateSerializer, Immutable]
public abstract record class TodoListEvent(DateTimeOffset Timestamp)
{
    public abstract string GetDescription();
}
