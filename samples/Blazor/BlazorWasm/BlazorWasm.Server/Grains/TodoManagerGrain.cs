using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace BlazorWasm.Grains
{
    public class TodoManagerGrain : Grain, ITodoManagerGrain
    {
        private readonly IPersistentState<State> state;

        private Guid GrainKey => this.GetPrimaryKey();

        public TodoManagerGrain([PersistentState("State")] IPersistentState<State> state)
        {
            this.state = state;
        }

        public override Task OnActivateAsync()
        {
            if (state.State.Items == null)
            {
                state.State.Items = new HashSet<Guid>();
            }

            return base.OnActivateAsync();
        }

        public async Task RegisterAsync(Guid itemKey)
        {
            state.State.Items.Add(itemKey);
            await state.WriteStateAsync();
        }

        public async Task UnregisterAsync(Guid itemKey)
        {
            state.State.Items.Remove(itemKey);
            await state.WriteStateAsync();
        }

        public Task<ImmutableArray<Guid>> GetAllAsync() =>
            Task.FromResult(ImmutableArray.CreateRange(state.State.Items));

        public class State
        {
            public HashSet<Guid> Items { get; set; }
        }
    }
}