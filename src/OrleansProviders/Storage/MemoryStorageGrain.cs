/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans;
using Orleans.Storage;

namespace OrleansProviders.Storage
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
