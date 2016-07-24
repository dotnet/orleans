using System;

namespace Samples.StorageProviders
{
    /// <summary>
    /// Defines the interface for the lower level of JSON storage providers, i.e.
    /// the part that writes JSON strings to the underlying storage. The higher level
    /// maps between grain state data and JSON.
    /// </summary>
    /// <remarks>
    /// Having this interface allows most of the serialization-level logic
    /// to be implemented in a base class of the storage providers.
    /// </remarks>
    public interface IJSONStateDataManager : IDisposable
    {
        /// <summary>
        /// Deletes the grain state associated with a given key from the collection
        /// </summary>
        /// <param name="collectionName">The name of a collection, such as a type name</param>
        /// <param name="key">The primary key of the object to delete</param>
        System.Threading.Tasks.Task Delete(string collectionName, string key);

        /// <summary>
        /// Reads grain state from storage.
        /// </summary>
        /// <param name="collectionName">The name of a collection, such as a type name.</param>
        /// <param name="key">The primary key of the object to read.</param>
        /// <returns>A string containing a JSON representation of the entity, if it exists; null otherwise.</returns>
        System.Threading.Tasks.Task<string> Read(string collectionName, string key);

        /// <summary>
        /// Writes grain state to storage.
        /// </summary>
        /// <param name="collectionName">The name of a collection, such as a type name.</param>
        /// <param name="key">The primary key of the object to write.</param>
        /// <param name="entityData">A string containing a JSON representation of the entity.</param>
        System.Threading.Tasks.Task Write(string collectionName, string key, string entityData);
    }
}
