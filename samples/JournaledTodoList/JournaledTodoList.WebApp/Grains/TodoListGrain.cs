using System.Collections.Immutable;
using JournaledTodoList.WebApp.Grains.Events;
using Orleans.EventSourcing;

namespace JournaledTodoList.WebApp.Grains;

public sealed class TodoListGrain : JournaledGrain<TodoListGrain.TodoListProjection, TodoListEvent>, ITodoListGrain
{
    public async Task<ImmutableArray<TodoListEvent>> GetHistoryAsync()
    {
        var events = await RetrieveConfirmedEvents(0, Version);
        return events.ToImmutableArray();
    }

    public async Task<TodoList?> GetTodoListAtTimestampAsync(DateTimeOffset timestamp)
    {
        // Get all events up to the current version
        var allEvents = await GetHistoryAsync();

        // Create a fresh projection and apply the filtered events
        var historicalProjection = new TodoListProjection();
        foreach (var evt in allEvents.Where(e => e.Timestamp <= timestamp))
        {
            TransitionState(historicalProjection, evt);
        }

        // Only return a TodoList if there were events that matched the timestamp.
        return historicalProjection.Timestamp > DateTimeOffset.MinValue
            ? new TodoList(
                Name: this.GetPrimaryKeyString(),
                Items: historicalProjection.Items.Values.OrderBy(x => x.Id).ToImmutableArray(),
                Timestamp: historicalProjection.Timestamp)
            : null;
    }

    public async Task<TodoList> GetTodoListAsync()
    {
        var list = new TodoList(
            Name: this.GetPrimaryKeyString(),
            Items: State.Items.Values.OrderBy(x => x.Id).ToImmutableArray(),
            Timestamp: State.Timestamp);

        return list;
    }

    public async Task AddTodoItemAsync(string title)
    {
        var evt = new TodoItemAdded(
            Version,
            DateTimeOffset.UtcNow,
            title);

        RaiseEvent(evt);
        await ConfirmEvents();
    }

    public async Task UpdateTodoItemAsync(int id, string title)
    {
        var evt = new TodoItemUpdated(id, DateTimeOffset.UtcNow, title);
        RaiseEvent(evt);
        await ConfirmEvents();
    }

    public async Task ToggleTodoItemAsync(int id)
    {
        var evt = new TodoItemToggled(id, DateTimeOffset.UtcNow);
        RaiseEvent(evt);
        await ConfirmEvents();
    }

    public async Task RemoveTodoItemAsync(int id)
    {
        var evt = new TodoItemRemoved(id, DateTimeOffset.UtcNow);
        RaiseEvent(evt);
        await ConfirmEvents();
    }

    public async Task SetNameAsync(string listName)
    {
        var evt = new TodoListNameChanged(listName, DateTimeOffset.UtcNow);
        RaiseEvent(evt);
        await ConfirmEvents();

        // Publish list with new name to todo list registry.
        // The registry only cares about the id and name of the list,
        // so this is the only place where we need to interact with the registry.
        var registry = GrainFactory.GetGrain<ITodoListRegistryGrain>(Constants.TodoListRegistryId);
        await registry.RegisterTodoListAsync(new TodoListReference(this.GetPrimaryKeyString(), listName));
    }

    /// <summary>
    /// The state container for <see cref="TodoListGrain"/>.
    /// NOTE: this has to be a mutable object.
    /// </summary>
    public sealed class TodoListProjection
    {
        public Dictionary<int, TodoItem> Items { get; set; } = [];

        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.MinValue;

        public string Name { get; set; } = string.Empty;

        public void Apply(TodoItemAdded added)
        {
            Items.Add(added.ItemId, new TodoItem(added.ItemId, added.Title, false));
            Timestamp = added.Timestamp;
        }

        public void Apply(TodoItemUpdated updated)
        {
            if (Items.TryGetValue(updated.ItemId, out var item))
            {
                Items[updated.ItemId] = item with { Title = updated.Title };
            }
            Timestamp = updated.Timestamp;
        }

        public void Apply(TodoItemToggled toggled)
        {
            if (Items.TryGetValue(toggled.ItemId, out var item))
            {
                Items[toggled.ItemId] = item with { IsCompleted = !item.IsCompleted };
            }
            Timestamp = toggled.Timestamp;
        }

        public void Apply(TodoItemRemoved removed)
        {
            Items.Remove(removed.ItemId);
            Timestamp = removed.Timestamp;
        }

        public void Apply(TodoListNameChanged nameChanged)
        {
            Name = nameChanged.Name;
            Timestamp = nameChanged.Timestamp;
        }
    }
}
