using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace BlazorServer
{
    public class TodoManagerGrain : Grain, ITodoManagerGrain
    {
        private readonly IPersistentState<State> _state;

        public TodoManagerGrain([PersistentState("State")] IPersistentState<State> state)
        {
            this._state = state;
        }

        public override Task OnActivateAsync()
        {
            if (_state.State.Items == null)
            {
                _state.State.Items = new HashSet<Guid>();
            }

            return base.OnActivateAsync();
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

        public class State
        {
            public HashSet<Guid> Items { get; set; }
        }
    }
}