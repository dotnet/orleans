using BlazorServer.Models;

namespace BlazorServer;

public class TodoGrain(
    ILogger<TodoGrain> logger,
    [PersistentState("State")] IPersistentState<TodoGrain.State> state) : Grain, ITodoGrain
{
    private string GrainType => nameof(TodoGrain);

    private Guid GrainKey => this.GetPrimaryKey();

    public Task<TodoItem?> GetAsync() => Task.FromResult(state.State.Item);

    public async Task SetAsync(TodoItem item)
    {
        // Ensure the key is consistent
        if (item.Key != GrainKey)
        {
            throw new InvalidOperationException();
        }

        // Save the item
        state.State = state.State with { Item = item };
        await state.WriteStateAsync();

        // Register the item with its owner list
        await GrainFactory.GetGrain<ITodoManagerGrain>(item.OwnerKey)
            .RegisterAsync(item.Key);

        // For sample debugging
        logger.LogInformation(
            "{@GrainType} {@GrainKey} now contains {@Todo}",
            GrainType, GrainKey, item);

        // Notify listeners - best effort only
        var streamId = StreamId.Create(nameof(ITodoGrain), item.OwnerKey);
        this.GetStreamProvider("MemoryStreams").GetStream<TodoNotification>(streamId)
            .OnNextAsync(new TodoNotification(item.Key, item))
            .Ignore();
    }

    public async Task ClearAsync()
    {
        // Fast path for already cleared state
        if (state.State.Item is null) return;

        // Hold on to the keys
        var itemKey = state.State.Item.Key;
        var ownerKey = state.State.Item.OwnerKey;

        // Unregister from the registry
        await GrainFactory.GetGrain<ITodoManagerGrain>(ownerKey)
            .UnregisterAsync(itemKey);

        // Clear the state
        await state.ClearStateAsync();

        // For sample debugging
        logger.LogInformation(
            "{@GrainType} {@GrainKey} is now cleared",
            GrainType, GrainKey);

        // Notify listeners - best effort only
        var streamId = StreamId.Create(nameof(ITodoGrain), ownerKey);
        this.GetStreamProvider("MemoryStreams").GetStream<TodoNotification>(streamId)
            .OnNextAsync(new TodoNotification(itemKey, null))
            .Ignore();

        // No need to stay alive anymore
        DeactivateOnIdle();
    }

    [GenerateSerializer, Immutable]
    public sealed record class State
    {
        [Id(0)]
        public TodoItem? Item { get; init; }
    }
}
