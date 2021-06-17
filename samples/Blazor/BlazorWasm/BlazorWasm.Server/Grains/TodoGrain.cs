using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using BlazorWasm.Models;

namespace BlazorWasm.Grains
{
    public class TodoGrain : Grain, ITodoGrain
    {
        private readonly ILogger<TodoGrain> logger;
        private readonly IPersistentState<State> state;

        private string GrainType => nameof(TodoGrain);
        private Guid GrainKey => this.GetPrimaryKey();

        public TodoGrain(ILogger<TodoGrain> logger, [PersistentState("State")] IPersistentState<State> state)
        {
            this.logger = logger;
            this.state = state;
        }

        public Task<TodoItem> GetAsync() => Task.FromResult(state.State.Item);

        public async Task SetAsync(TodoItem item)
        {
            // ensure the key is consistent
            if (item.Key != GrainKey)
            {
                throw new InvalidOperationException();
            }

            // save the item
            state.State.Item = item;
            await state.WriteStateAsync();

            // register the item with its owner list
            await GrainFactory.GetGrain<ITodoManagerGrain>(item.OwnerKey)
                .RegisterAsync(item.Key);

            // for sample debugging
            logger.LogInformation(
                "{@GrainType} {@GrainKey} now contains {@Todo}",
                GrainType, GrainKey, item);

            // notify listeners - best effort only
            GetStreamProvider("SMS").GetStream<TodoNotification>(item.OwnerKey, nameof(ITodoGrain))
                .OnNextAsync(new TodoNotification(item.Key, item))
                .Ignore();
        }

        public async Task ClearAsync()
        {
            // fast path for already cleared state
            if (state.State.Item == null) return;

            // hold on to the keys
            var itemKey = state.State.Item.Key;
            var ownerKey = state.State.Item.OwnerKey;

            // unregister from the registry
            await GrainFactory.GetGrain<ITodoManagerGrain>(ownerKey)
                .UnregisterAsync(itemKey);

            // clear the state
            await state.ClearStateAsync();

            // for sample debugging
            logger.LogInformation(
                "{@GrainType} {@GrainKey} is now cleared",
                GrainType, GrainKey);

            // notify listeners - best effort only
            GetStreamProvider("SMS").GetStream<TodoNotification>(ownerKey, nameof(ITodoGrain))
                .OnNextAsync(new TodoNotification(itemKey, null))
                .Ignore();

            // no need to stay alive anymore
            DeactivateOnIdle();
        }

        public class State
        {
            public TodoItem Item { get; set; }
        }
    }
}