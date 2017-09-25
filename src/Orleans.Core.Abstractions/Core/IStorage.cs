using System.Threading.Tasks;

namespace Orleans.Core
{
    public interface IStorage<TState>
        where TState : new()
    {
        TState State { get; set; }

        /// <summary>
        /// Async method to cause the current grain state data to be cleared and reset. 
        /// This will usually mean the state record is deleted from backing store, but the specific behavior is defined by the storage provider instance configured for this grain.
        /// If Etags do not match, then this operation will fail; Set Etag = <c>null</c> to indicate "always delete".
        /// </summary>
        Task ClearStateAsync();

        /// <summary>
        /// Async method to cause write of the current grain state data into backing store.
        /// If Etags do not match, then this operation will fail; Set Etag = <c>null</c> to indicate "always overwrite".
        /// </summary>
        Task WriteStateAsync();

        /// <summary>
        /// Async method to cause refresh of the current grain state data from backing store.
        /// Any previous contents of the grain state data will be overwritten.
        /// </summary>
        Task ReadStateAsync();
    }
}
