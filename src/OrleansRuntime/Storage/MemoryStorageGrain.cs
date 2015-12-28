using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

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
        private Logger logger;

        public override Task OnActivateAsync()
        {
            grainStore = new Dictionary<string, GrainStateStore>();
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation for MemoryStorageGrain virtually indefinitely.
            logger = GetLogger(GetType().Name);
            logger.Info("OnActivateAsync");
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            grainStore = null;
            return TaskDone.Done;
        }

        public Task<Tuple<IDictionary<string, object>, string>> ReadStateAsync(string stateStore, string grainStoreKey)
        {
            if (logger.IsVerbose) logger.Verbose("ReadStateAsync for {0} grain: {1}", stateStore, grainStoreKey);
            GrainStateStore storage = GetStoreForGrain(stateStore);
            var stateTuple = storage.GetGrainState(grainStoreKey);
            return Task.FromResult(stateTuple);
        }

        public Task<string> WriteStateAsync(string stateStore, string grainStoreKey, IDictionary<string, object> grainState, string eTag)
        {
            if (logger.IsVerbose) logger.Verbose("WriteStateAsync for {0} grain: {1} eTag: {2}", stateStore, grainStoreKey, eTag);
            GrainStateStore storage = GetStoreForGrain(stateStore);
            string newETag = storage.UpdateGrainState(grainStoreKey, grainState, eTag);
            if (logger.IsVerbose) logger.Verbose("Done WriteStateAsync for {0} grain: {1} eTag: {2}", stateStore, grainStoreKey, eTag);
            return Task.FromResult(newETag);
        }

        public Task DeleteStateAsync(string stateStore, string grainStoreKey, string eTag)
        {
            if (logger.IsVerbose) logger.Verbose("DeleteStateAsync for {0} grain: {1} eTag: {2}", stateStore, grainStoreKey, eTag);
            GrainStateStore storage = GetStoreForGrain(stateStore);
            storage.DeleteGrainState(grainStoreKey, eTag);
            if (logger.IsVerbose) logger.Verbose("Done DeleteStateAsync for {0} grain: {1} eTag: {2}", stateStore, grainStoreKey, eTag);
            return TaskDone.Done;
        }

        private GrainStateStore GetStoreForGrain(string grainType)
        {
            GrainStateStore storage;
            if (!grainStore.TryGetValue(grainType, out storage))
            {
                storage = new GrainStateStore(logger);
                grainStore.Add(grainType, storage);
            }
            return storage;
        }

        private class GrainStateStore
        {
            private Logger logger;
            private readonly IDictionary<string, Tuple<IDictionary<string, object>, long>> grainStateStorage = new Dictionary<string, Tuple<IDictionary<string, object>, long>>();
            
            public GrainStateStore(Logger logger)
            {
                this.logger = logger;
            }

            public Tuple<IDictionary<string, object>, string> GetGrainState(string grainId)
            {
                Tuple<IDictionary<string, object>, long> state;
                if(grainStateStorage.TryGetValue(grainId, out state))
                    return Tuple.Create(state.Item1, state.Item2.ToString());
                else
                    return Tuple.Create<IDictionary<string, object>, string>(null, null); // upon first read, return null/invalid etag, to mimic Azure Storage.
            }

            public string UpdateGrainState(string grainStoreKey, IDictionary<string, object> state, string receivedEtag)
            {
                long currentETag = 0;
                Tuple<IDictionary<string, object>, long> oldState;
                if (grainStateStorage.TryGetValue(grainStoreKey, out oldState)) {
                    currentETag = oldState.Item2;
                }
                ValidateEtag(currentETag, receivedEtag, grainStoreKey, "Update");
                currentETag++;
                grainStateStorage[grainStoreKey] = Tuple.Create(state, currentETag);
                return currentETag.ToString();
            }

            public void DeleteGrainState(string grainStoreKey, string receivedEtag)
            {
                long currentETag = 0;
                Tuple<IDictionary<string, object>, long> oldState;
                if (grainStateStorage.TryGetValue(grainStoreKey, out oldState)){
                    currentETag = oldState.Item2;
                }
                ValidateEtag(currentETag, receivedEtag, grainStoreKey, "Delete");
                grainStateStorage.Remove(grainStoreKey);
            }

            private void ValidateEtag(long currentETag, string receivedEtag, string grainStoreKey, string operation)
            {
                if (receivedEtag == null) // first write
                {
                    if (currentETag > 0)
                    {
                        string error = string.Format("Etag mismatch during {0} for grain {1}: Expected = {2} Received = null", operation, grainStoreKey, currentETag.ToString());
                        logger.Warn(0, error);
                        new InconsistentStateException(error);
                    }
                }
                else // non first write
                {
                    if (receivedEtag != currentETag.ToString())
                    {
                        string error = string.Format("Etag mismatch during {0} for grain {1}: Expected = {2} Received = {3}", operation, grainStoreKey, currentETag.ToString(), receivedEtag);
                        logger.Warn(0, error);
                        throw new InconsistentStateException(error);
                    }
                }
            }
        }
    }
}
