using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Storage
{

    /// <summary>
    /// Implementaiton class for the Storage Grain used by In-memory Storage Provider
    /// </summary>
    /// <seealso cref="MemoryStorage"/>
    /// <seealso cref="IMemoryStorageGrain"/>
    internal class MemoryStorageGrain : Grain, IMemoryStorageGrain
    {
        private IDictionary<string, GrainStateStore> grainStore;

        public override Task OnActivateAsync()
        {
            grainStore = new Dictionary<string, GrainStateStore>();
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation for MemoryStorageGrain virtually indefinitely.
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            grainStore = null;
            return TaskDone.Done;
        }

        public Task<IDictionary<string, object>> ReadStateAsync(string grainType, string grainId)
        {
            GrainStateStore storage = GetStoreForGrain(grainType);
            var state = storage.GetGrainState(grainId);
            return Task.FromResult(state);
        }

        public Task WriteStateAsync(string grainType, string grainId, IDictionary<string, object> grainState)
        {
            GrainStateStore storage = GetStoreForGrain(grainType);
            storage.UpdateGrainState(grainId, grainState);
            return TaskDone.Done;
        }

        public Task DeleteStateAsync(string grainType, string grainId)
        {
            GrainStateStore storage = GetStoreForGrain(grainType);
            storage.DeleteGrainState(grainId);
            return TaskDone.Done;
        }

        private GrainStateStore GetStoreForGrain(string grainType)
        {
            GrainStateStore storage;
            if (!grainStore.TryGetValue(grainType, out storage))
            {
                storage = new GrainStateStore();
                grainStore.Add(grainType, storage);
            }
            return storage;
        }

        private class GrainStateStore
        {
            private readonly IDictionary<string, IDictionary<string, object>> grainStateStorage = new Dictionary<string, IDictionary<string, object>>();

            public IDictionary<string, object> GetGrainState(string grainId)
            {
                IDictionary<string, object> state;
                grainStateStorage.TryGetValue(grainId, out state);
                return state;
            }

            public void UpdateGrainState(string grainId, IDictionary<string, object> state)
            {
                grainStateStorage[grainId] = state;
            }

            public void DeleteGrainState(string grainId)
            {
                grainStateStorage.Remove(grainId);
            }
        }
    }
}
