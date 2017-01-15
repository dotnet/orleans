using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Storage
{

    /// <summary>
    /// Implementaiton class for the Storage Grain used by In-memory storage provider
    /// <c>Orleans.Storage.MemoryStorage</c>
    /// </summary>
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

        public Task<IGrainState> ReadStateAsync(string stateStore, string grainStoreKey)
        {
            if (logger.IsVerbose) logger.Verbose("ReadStateAsync for {0} grain: {1}", stateStore, grainStoreKey);
            GrainStateStore storage = GetStoreForGrain(stateStore);
            var grainState = storage.GetGrainState(grainStoreKey);
            return Task.FromResult(grainState);
        }
        
        public Task<string> WriteStateAsync(string stateStore, string grainStoreKey, IGrainState grainState)
        {
            if (logger.IsVerbose) logger.Verbose("WriteStateAsync for {0} grain: {1} eTag: {2}", stateStore, grainStoreKey, grainState.ETag);
            GrainStateStore storage = GetStoreForGrain(stateStore);
            storage.UpdateGrainState(grainStoreKey, grainState);
            if (logger.IsVerbose) logger.Verbose("Done WriteStateAsync for {0} grain: {1} eTag: {2}", stateStore, grainStoreKey, grainState.ETag);
            return Task.FromResult(grainState.ETag);
        }

        public Task DeleteStateAsync(string grainType, string grainId, string etag)
        {
            GrainStateStore storage = GetStoreForGrain(grainType);
            storage.DeleteGrainState(grainId, etag);
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
            private readonly Logger logger;
            public GrainStateStore(Logger logger)
            {
                this.logger = logger;
            }
            private readonly IDictionary<string, IGrainState> grainStateStorage = new Dictionary<string, IGrainState>();

            public IGrainState GetGrainState(string grainId)
            {
                IGrainState entry;
                grainStateStorage.TryGetValue(grainId, out entry);
                return ReferenceEquals(entry, Deleted) ? null : entry;
            }

            public void UpdateGrainState(string grainId, IGrainState grainState)
            {
                IGrainState entry;
                string currentETag = null;
                if (grainStateStorage.TryGetValue(grainId, out entry))
                {
                    currentETag = entry.ETag;
                }

                ValidateEtag(currentETag, grainState.ETag, grainId, "Update");
                grainState.ETag = NewEtag();
                grainStateStorage[grainId] = grainState;
            }

            public void DeleteGrainState(string grainId, string receivedEtag)
            {
                IGrainState entry;
                string currentETag = null;
                if (grainStateStorage.TryGetValue(grainId, out entry))
                {
                    currentETag = entry.ETag;
                }

                ValidateEtag(currentETag, receivedEtag, grainId, "Delete");
                grainStateStorage[grainId] = Deleted;
            }

            private static string NewEtag()
            {
                return Guid.NewGuid().ToString("N");
            }

            private void ValidateEtag(string currentETag, string receivedEtag, string grainStoreKey, string operation)
            {
                // if we have no current etag, we will accept the users data.
                // This is a mitigation for when the memory storage grain is lost due to silo crash.
                if (currentETag == null)
                    return;

                // if this is our first write, and we have an empty etag, we're good
                if (string.IsNullOrEmpty(currentETag) && receivedEtag == null)
                    return;

                // if current state and new state have matching etags, we're good
                if (receivedEtag == currentETag)
                    return;

                // else we have an etag mismatch
                string error = $"Etag mismatch during {operation} for grain {grainStoreKey}: Expected = {currentETag ?? "null"} Received = {receivedEtag}";
                logger.Warn(0, error);
                throw new InconsistentStateException(error);
            }

            /// <summary>
            /// Marker to record deleted state so we can detect the difference between deleted state and state that never existed.
            /// </summary>
            private class DeletedState : IGrainState
            {
                public DeletedState()
                {
                    ETag = string.Empty;
                }
                public object State { get; set; }
                public string ETag { get; set; }
            }
            private static readonly IGrainState Deleted = new DeletedState();
        }
    }
}
