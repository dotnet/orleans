using Orleans.Runtime;
using BlazorWasm.Models;

namespace BlazorWasm.Grains;

public class TodoGrain : Grain, ITodoGrain
{
    private readonly ILogger<TodoGrain> _logger;
    private readonly IPersistentState<State> _state;

    private string GrainType => nameof(TodoGrain);
    private Guid GrainKey => this.GetPrimaryKey();

    public TodoGrain(
        ILogger<TodoGrain> logger,
        [PersistentState("State")] IPersistentState<State> state)
    {
        _logger = logger;
        _state = state;
    }

    public Task<TodoItem?> GetAsync() => Task.FromResult(_state.State.Item);

    public async Task SetAsync(TodoItem item)
    {
        // ensure the key is consistent
        if (item.Key != GrainKey)
        {
            throw new InvalidOperationException();
        }

        // save the item
        _state.State.Item = item;
        await _state.WriteStateAsync();

        // register the item with its owner list
        await GrainFactory.GetGrain<ITodoManagerGrain>(item.OwnerKey)
            .RegisterAsync(item.Key);

        // for sample debugging
        _logger.LogInformation(
            "{@GrainType} {@GrainKey} now contains {@Todo}",
            GrainType, GrainKey, item);

        // notify listeners - best effort only
        this.GetStreamProvider("MemoryStreams")
            .GetStream<TodoNotification>(StreamId.Create(nameof(ITodoGrain), item.OwnerKey))
            .OnNextAsync(new TodoNotification(item.Key, item))
            .Ignore();
    }

    public async Task ClearAsync()
    {
        // fast path for already cleared state
        if (_state.State.Item is null) return;

        // hold on to the keys
        var itemKey = _state.State.Item.Key;
        var ownerKey = _state.State.Item.OwnerKey;

        // unregister from the registry
        await GrainFactory.GetGrain<ITodoManagerGrain>(ownerKey)
            .UnregisterAsync(itemKey);

        // clear the state
        await _state.ClearStateAsync();

        // for sample debugging
        _logger.LogInformation(
            "{@GrainType} {@GrainKey} is now cleared",
            GrainType, GrainKey);

        // notify listeners - best effort only
        this.GetStreamProvider("MemoryStreams")
            .GetStream<TodoNotification>(StreamId.Create(nameof(ITodoGrain), itemKey))
            .OnNextAsync(new TodoNotification(itemKey, null))
            .Ignore();

        // no need to stay alive anymore
        DeactivateOnIdle();
    }

    [GenerateSerializer]
    public class State
    {
        [Id(0)]
        public TodoItem? Item { get; set; }
    }
}
