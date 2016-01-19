using System.Threading.Tasks;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain interface for internal memory storage grain used by Orleans in-memory storage provider.
    /// </summary>
    public interface IMemoryStorageGrain : IGrainWithIntegerKey
    {
        /// <summary>
        /// Async method to cause retrieval of the specified grain state data from memory store.
        /// </summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainId">Grain id for this grain.</param>
        /// <returns>Value promise for the currently stored grain state for the specified grain.</returns>
        Task<IGrainState> ReadStateAsync(string stateStore, string grainStoreKey);

        /// <summary>
        /// Async method to cause update of the specified grain state data into memory store.
        /// </summary>
        /// <param name="stateStore">The name of the store that is used to store this grain state</param>
        /// <param name="grainStoreKey">Store key for this grain.</param>
        /// <param name="grainState">New state data to be stored for this grain.</param>
        /// <returns>Completion promise with new eTag for the update operation for stored grain state for the specified grain.</returns>
        Task<string> WriteStateAsync(string grainType, string grainId, IGrainState grainState);
        
        /// <param name="stateStore">The name of the store that is used to store this grain state</param>
        /// <param name="grainStoreKey">Store key for this grain.</param>
        /// <param name="eTag">The previous etag that was read.</param>
        /// <returns>Completion promise for the update operation for stored grain state for the specified grain.</returns>
        Task DeleteStateAsync(string stateStore, string grainStoreKey, string eTag);
    }
}
