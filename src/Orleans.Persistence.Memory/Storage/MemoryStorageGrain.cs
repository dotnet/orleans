using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        private readonly Dictionary<(string, string), object> _store = new();
        private readonly ILogger _logger;

        public MemoryStorageGrain(ILogger<MemoryStorageGrain> logger)
        {
            _logger = logger;
        }

        public Task<IGrainState<T>> ReadStateAsync<T>(string stateStore, string grainStoreKey)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("ReadStateAsync for {StateStore} grain: {GrainStoreKey}", stateStore, grainStoreKey);

            _store.TryGetValue((stateStore, grainStoreKey), out var entry);
            return Task.FromResult((IGrainState<T>)entry);
        }

        public Task<string> WriteStateAsync<T>(string stateStore, string grainStoreKey, IGrainState<T> grainState)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("WriteStateAsync for {StateStore} grain: {GrainStoreKey} eTag: {ETag}", stateStore, grainStoreKey, grainState.ETag);

            var currentETag = GetETagFromStorage<T>(stateStore, grainStoreKey);
            ValidateEtag(currentETag, grainState.ETag, grainStoreKey, "Update");
            grainState.ETag = NewEtag();
            _store[(stateStore, grainStoreKey)] = grainState;
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Done WriteStateAsync for {StateStore} grain: {GrainStoreKey} eTag: {ETag}", stateStore, grainStoreKey, grainState.ETag);
            return Task.FromResult(grainState.ETag);
        }

        public Task DeleteStateAsync<T>(string stateStore, string grainStoreKey, string etag)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("DeleteStateAsync for {StateStore} grain: {GrainStoreKey} eTag: {ETag}", stateStore, grainStoreKey, etag);

            var currentETag = GetETagFromStorage<T>(stateStore, grainStoreKey);
            ValidateEtag(currentETag, etag, grainStoreKey, "Delete");
            // Do not remove it from the dictionary, just set the value to null to remember that this item
            // was once in the store, and now is deleted
            _store[(stateStore, grainStoreKey)] = null; 
            return Task.CompletedTask;
        }

        private static string NewEtag()
        {
            return Guid.NewGuid().ToString("N");
        }

        private string GetETagFromStorage<T>(string grainType, string grainId)
        {
            string currentETag = null;
            if (_store.TryGetValue((grainType, grainId), out var entry))
            {
                // If the entry is null, it was removed from storage
                currentETag = entry != null ? ((IGrainState<T>)entry).ETag : string.Empty;
            }
            return currentETag;
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
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(0, "Etag mismatch during {Operation} for grain {GrainStoreKey}: Expected = {Expected} Received = {Received}", operation, grainStoreKey, currentETag, receivedEtag);
            }

            throw new MemoryStorageEtagMismatchException(currentETag, receivedEtag);
        }
    }
}
