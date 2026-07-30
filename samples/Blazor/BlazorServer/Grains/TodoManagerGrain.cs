using System.Collections.Immutable;

namespace BlazorServer;

public class TodoManagerGrain([PersistentState("State")] IPersistentState<TodoManagerGrain.State> state)
    : Grain, ITodoManagerGrain
{
    public async Task RegisterAsync(Guid itemKey)
    {
        state.State = state.State with { Items = state.State.Items.Add(itemKey) };
        await state.WriteStateAsync();
    }

    public async Task UnregisterAsync(Guid itemKey)
    {
        state.State = state.State with { Items = state.State.Items.Remove(itemKey) };
        await state.WriteStateAsync();
    }

    public Task<ImmutableHashSet<Guid>> GetAllAsync()
        => Task.FromResult(state.State.Items);

    [GenerateSerializer, Immutable]
    public record class State
    {
        [Id(0)]
        public ImmutableHashSet<Guid> Items { get; init; } = [];
    }
}
