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

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain interface for internal memory storage grain used by Orleans in-memory storage provider.
    /// </summary>
    public interface IMemoryStorageGrain : IGrain
    {
        /// <summary>
        /// Async method to cause retrieval of the specified grain state data from memory store.
        /// </summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainId">Grain id for this grain.</param>
        /// <returns>Value promise for the currently stored grain state for the specified grain.</returns>
        Task<IDictionary<string, object>> ReadStateAsync(string grainType, string grainId);
        
        /// <summary>
        /// Async method to cause update of the specified grain state data into memory store.
        /// </summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainId">Grain id for this grain.</param>
        /// <param name="grainState">New state data to be stored for this grain.</param>
        /// <returns>Completion promise for the update operation for stored grain state for the specified grain.</returns>
        Task WriteStateAsync(string grainType, string grainId, IDictionary<string, object> grainState);

        /// <summary>
        /// Async method to cause deletion of the specified grain state data from memory store.
        /// </summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainId">Grain id for this grain.</param>
        /// <returns>Completion promise for the update operation for stored grain state for the specified grain.</returns>
        Task DeleteStateAsync(string grainType, string grainId);
    }
}
