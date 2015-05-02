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

namespace Orleans
{
    /// <summary>
    /// Base interface for interfaces that define persistent state properties of a grain.
    /// </summary>
    public interface IGrainState
    {
        /// <summary>
        /// Opaque value set by the storage provider representing an 'Etag' setting for the last time the state data was read from backing store.
        /// </summary>
        string Etag { get; set; }

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

        /// <summary>
        /// Return a snapshot of the current grain state data, as a Dictionary of Key-Value pairs.
        /// </summary>
        IDictionary<string, object> AsDictionary();

        /// <summary>
        /// Update the current grain state data with the specified Dictionary of Key-Value pairs.
        /// </summary>
        void SetAll(IDictionary<string, object> values);
    }
}