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
using System.Collections.Generic;


namespace Orleans.Runtime.GrainDirectory
{
    interface IGrainDirectoryCache<TValue>
    {
        /// <summary>
        /// Adds a new entry with the given version into the cache: key (grain) --> value
        /// The new entry will override any existing entry under the given key, 
        /// regardless of the stored version
        /// </summary>
        /// <param name="key">key to add</param>
        /// <param name="value">value to add</param>
        /// <param name="version">version for the value</param>
        void AddOrUpdate(GrainId key, TValue value, int version);

        /// <summary>
        /// Removes an entry from the cache given its key
        /// </summary>
        /// <param name="key">key to remove</param>
        /// <returns>True iff the enrty was in the cache and the removal was succeful</returns>
        bool Remove(GrainId key);
        
        /// <summary>
        /// Clear the cache, deleting all entries.
        /// </summary>
        void Clear();

        /// <summary>
        /// Looks uo the cached value and version by the given key
        /// </summary>
        /// <param name="key">key for the lookup</param>
        /// <param name="result">value if the key is found, undefined otherwise</param>
        /// <param name="version">version of cached value if the key is found, undefined otherwise</param>
        /// <returns>true iff the the given key is in the cache</returns>
        bool LookUp(GrainId key, out TValue result, out int version);

        /// <summary>
        /// Returns list of key-value-version tuples stored currently in the cache.
        /// </summary>
        IReadOnlyList<Tuple<GrainId, TValue, int>> KeyValues { get; }
    }

    internal static class GrainDirectoryCacheExtensions
    {
        /// <summary>
        /// Looks up the cached value by the given key.
        /// </summary>
        /// <param name="cache">grain directory cache to look up results from</param>
        /// <param name="key">key for the lookup</param>
        /// <param name="result">value if the key is found, undefined otherwise</param>
        /// <returns>true iff the the given key is in the cache</returns>
        public static bool LookUp<TValue>(this IGrainDirectoryCache<TValue> cache, GrainId key, out TValue result)
        {
            int version;
            return cache.LookUp(key, out result, out version);
        }
    }
}
