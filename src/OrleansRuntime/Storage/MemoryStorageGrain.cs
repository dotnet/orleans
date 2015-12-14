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

        public Task<Tuple<IDictionary<string, object>, string>> ReadStateAsync(string grainType, string grainId)
        {
            GrainStateStore storage = GetStoreForGrain(grainType);
            var stateTuple = storage.GetGrainState(grainId);
            return Task.FromResult(stateTuple);
        }

        public Task<string> WriteStateAsync(string grainType, string grainId, IDictionary<string, object> grainState, string eTag)
        {
            GrainStateStore storage = GetStoreForGrain(grainType);
            string newETag = storage.UpdateGrainState(grainId, grainState, eTag);
            return Task.FromResult(newETag);
        }

        public Task DeleteStateAsync(string grainType, string grainId, string eTag)
        {
            GrainStateStore storage = GetStoreForGrain(grainType);
            storage.DeleteGrainState(grainId, eTag);
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
            private readonly IDictionary<string, Tuple<IDictionary<string, object>, long>> grainStateStorage = new Dictionary<string, Tuple<IDictionary<string, object>, long>>();
            
            public Tuple<IDictionary<string, object>, string> GetGrainState(string grainId)
            {
                Tuple<IDictionary<string, object>, long> state;
                if(grainStateStorage.TryGetValue(grainId, out state))
                    return Tuple.Create(state.Item1, state.Item2.ToString());
                else
                    return Tuple.Create<IDictionary<string, object>, string>(null, null); // upon first read, return null/invalid etag, to mimic Azure Storage.
            }

            public string UpdateGrainState(string grainId, IDictionary<string, object> state, string receivedEtag)
            {
                long currentETag = 0;
                Tuple<IDictionary<string, object>, long> oldState;
                if (grainStateStorage.TryGetValue(grainId, out oldState)) {
                    currentETag = oldState.Item2;
                }
                ValidateEtag(currentETag, receivedEtag);
                currentETag++;
                grainStateStorage[grainId] = Tuple.Create(state, currentETag);
                return currentETag.ToString();
            }

            public void DeleteGrainState(string grainId, string receivedEtag)
            {
                long currentETag = 0;
                Tuple<IDictionary<string, object>, long> oldState;
                if (grainStateStorage.TryGetValue(grainId, out oldState)){
                    currentETag = oldState.Item2;
                }
                ValidateEtag(currentETag, receivedEtag);
                grainStateStorage.Remove(grainId);
            }

            private void ValidateEtag(long currentETag, string receivedEtag)
            {
                if (receivedEtag == null) // first write
                {
                    if (currentETag > 0) 
                        new InconsistentStateException(
                            string.Format("Etag mismatch durign Write: Expected = {0} Received = null", currentETag.ToString()));
                }
                else // non first write
                {
                    if (receivedEtag != currentETag.ToString())
                        throw new InconsistentStateException(
                            string.Format("Etag mismatch durign Write: Expected = {0} Received = {1}", currentETag.ToString(), receivedEtag));
                }
            }
        }
    }
}
