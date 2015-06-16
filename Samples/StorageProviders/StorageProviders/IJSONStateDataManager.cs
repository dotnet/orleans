/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
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
