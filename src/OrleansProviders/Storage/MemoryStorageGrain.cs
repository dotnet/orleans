using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Storage.Internal;

namespace Orleans.Storage
{

    /// <summary>
    /// Implementation class for the Storage Grain used by In-memory storage provider
    /// <c>Orleans.Storage.MemoryStorage</c>
    /// </summary>
    [KeepAlive]
    internal class MemoryStorageGrain : Grain, IMemoryStorageGrain
    {
        private Dictionary<(string, string), IGrainState> grainStore;
        private ILogger logger;

        public override Task OnActivateAsync()
        {
            grainStore = new Dictionary<(string, string), IGrainState>();
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation for MemoryStorageGrain virtually indefinitely.
            logger = this.ServiceProvider.GetRequiredService<ILogger<MemoryStorageGrain>>();
            logger.LogInformation("OnActivateAsync");
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            logger.LogInformation("OnDeactivateAsync");
            grainStore = null;
            return Task.CompletedTask;
        }

        public Task<IGrainState> ReadStateAsync(string stateStore, string grainStoreKey)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("ReadStateAsync for {StateStore} grain: {GrainStoreKey}", stateStore, grainStoreKey);
            grainStore.TryGetValue((stateStore, grainStoreKey), out var entry);
            return Task.FromResult(entry is DeletedState ? null : entry);
        }

        public Task<string> WriteStateAsync(string stateStore, string grainStoreKey, IGrainState grainState)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("WriteStateAsync for {StateStore} grain: {GrainStoreKey} eTag: {ETag}", stateStore, grainStoreKey, grainState.ETag);
            string currentETag = null;
            if (grainStore.TryGetValue((stateStore, grainStoreKey), out var entry))
            {
                currentETag = entry.ETag;
            }

            ValidateEtag(currentETag, grainState.ETag, grainStoreKey, "Update");
            grainState.ETag = NewEtag();
            grainStore[(stateStore, grainStoreKey)] = grainState;
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Done WriteStateAsync for {StateStore} grain: {GrainStoreKey} eTag: {ETag}", stateStore, grainStoreKey, grainState.ETag);
            return Task.FromResult(grainState.ETag);
        }

        public Task DeleteStateAsync(string grainType, string grainId, string etag)
        {
            string currentETag = null;
            if (grainStore.TryGetValue((grainType, grainId), out var entry))
            {
                currentETag = entry.ETag;
            }

            ValidateEtag(currentETag, etag, grainId, "Delete");
            grainStore[(grainType, grainId)] = deleted;
            return Task.CompletedTask;
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

            // if current state and new state have matching etags, or we're to ignore the ETag, we're good
            if (receivedEtag == currentETag || receivedEtag == StorageProviderUtils.ANY_ETAG)
                return;

            // else we have an etag mismatch
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(0, "Etag mismatch during {Operation} for grain {GrainStoreKey}: Expected = {Expected} Received = {Received}", operation, grainStoreKey, currentETag, receivedEtag);
            }
            throw new MemoryStorageEtagMismatchException(currentETag, receivedEtag);
        }

        /// <summary>
        /// Marker to record deleted state so we can detect the difference between deleted state and state that never existed.
        /// </summary>
        private sealed class DeletedState : IGrainState
        {
            public object State { get; set; }
            public Type Type => typeof(object);
            public string ETag { get; set; } = string.Empty;
            public bool RecordExists { get; set; }
        }
        private readonly IGrainState deleted = new DeletedState();
    }
}
