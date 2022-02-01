using System.Threading.Tasks;

namespace Orleans.Core
{
    /// <summary>
    /// Provides method for operating on grain storage.
    /// </summary>
    public interface IStorage
    {
        /// <summary>
        /// Gets the ETag.
        /// </summary>
        /// <remarks>
        /// An ETag, or entity tag, is a value used to prevent concurrent writes where one or more of those writes has not first observed the most recent operation.
        /// </remarks>
        string Etag { get; }

        /// <summary>
        /// Gets a value indicating whether the record already exists.
        /// </summary>
        bool RecordExists { get; }

        /// <summary>
        /// Clears the grain state.
        /// </summary>
        /// <remarks>
        /// This will usually mean the state record is deleted from backing store, but the specific behavior is defined by the storage provider instance configured for this grain.
        /// If the Etag does not match what is present in the backing store, then this operation will fail; Set <see cref="Etag"/> to <see langword="null"/> to indicate "always delete".
        /// </remarks>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        Task ClearStateAsync();

        /// <summary>
        /// Writes grain state to storage.
        /// </summary>
        /// <remarks>
        /// If the Etag does not match what is present in the backing store, then this operation will fail; Set <see cref="Etag"/> to <see langword="null"/> to indicate "always delete".
        /// </remarks>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        Task WriteStateAsync();

        /// <summary>
        /// Reads grain state from storage.
        /// </summary>
        /// <remarks>
        /// Any previous contents of the grain state data will be overwritten.
        /// </remarks>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        Task ReadStateAsync();
    }

    /// <summary>
    /// Provides method for operating on grain state.
    /// </summary>
    public interface IStorage<TState> : IStorage
    {
        /// <summary>
        /// Gets or sets the state.
        /// </summary>
        TState State { get; set; }
    }
}