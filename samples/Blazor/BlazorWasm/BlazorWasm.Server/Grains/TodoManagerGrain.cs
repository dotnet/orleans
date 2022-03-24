using Orleans;
using Orleans.Runtime;
using System.Collections.Immutable;

namespace BlazorWasm.Grains;

public class TodoManagerGrain : Grain, ITodoManagerGrain
{
    private readonly IPersistentState<State> _state;

    private Guid GrainKey => this.GetPrimaryKey();

    public TodoManagerGrain(
        [PersistentState("State")] IPersistentState<State> state) => _state = state;

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

    public class State
    {
        public HashSet<Guid> Items { get; set; } = new();
    }
}
