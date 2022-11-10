using System.Collections.Immutable;
using Orleans.Runtime;

namespace BlazorServer;

public class TodoManagerGrain : Grain, ITodoManagerGrain
{
    private readonly IPersistentState<State> _state;

    public TodoManagerGrain(
        [PersistentState("State")] IPersistentState<State> state)
    {
        _state = state;
    }

    public async Task RegisterAsync(Guid itemKey)
    {
        _state.State.Items.Add(itemKey);
        await _state.WriteStateAsync();
    }

    public async Task UnregisterAsync(Guid itemKey)
    {
        _state.State.Items.Remove(itemKey);
        await _state.WriteStateAsync();
    }

    public Task<ImmutableArray<Guid>> GetAllAsync() =>
        Task.FromResult(ImmutableArray.CreateRange(_state.State.Items));

    [GenerateSerializer]
    public class State
    {
        [Id(0)]
        public HashSet<Guid> Items { get; set; } = new();
    }
}
