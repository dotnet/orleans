using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain interface for internal memory storage grain used by Orleans in-memory storage provider.
    /// </summary>
    public interface IMemoryStorageGrain : IGrainWithIntegerKey
    {
        /// <summary>Async method to cause retrieval of the specified grain state data from memory store.</summary>
        /// <param name="grainStoreKey">Store key for this grain.</param>
        /// <returns>Value promise for the currently stored grain state for the specified grain.</returns>
        [Alias("ReadStateAsync")]
        Task<IGrainState<T>?> ReadStateAsync<T>(string grainStoreKey);

        [Alias("45659318")]
        Task<IGrainState<T>?> ReadStateAsync<T>(string grainStoreKey, CancellationToken cancellationToken)
            => ReadStateAsync<T>(grainStoreKey);

        /// <summary>Async method to cause update of the specified grain state data into memory store.</summary>
        /// <param name="grainStoreKey">Grain ID.</param>
        /// <param name="grainState">New state data to be stored for this grain.</param>
        /// <returns>Completion promise with new eTag for the update operation for stored grain state for the specified grain.</returns>
        [Alias("WriteStateAsync")]
        Task<string> WriteStateAsync<T>(string grainStoreKey, IGrainState<T> grainState);

        [Alias("7CC6CA25")]
        Task<string> WriteStateAsync<T>(
            string grainStoreKey,
            IGrainState<T> grainState,
            CancellationToken cancellationToken)
            => WriteStateAsync(grainStoreKey, grainState);

        /// <param name="grainStoreKey">Store key for this grain.</param>
        /// <param name="eTag">The previous etag that was read.</param>
        /// <returns>Completion promise for the update operation for stored grain state for the specified grain.</returns>
        [Alias("DeleteStateAsync")]
        Task DeleteStateAsync<T>(string grainStoreKey, string? eTag);

        [Alias("B7CADD03")]
        Task DeleteStateAsync<T>(string grainStoreKey, string? eTag, CancellationToken cancellationToken)
            => DeleteStateAsync<T>(grainStoreKey, eTag);
    }
}
