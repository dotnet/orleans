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
    internal class LRUBasedGrainDirectoryCache<TValue> : IGrainDirectoryCache<TValue>
    {
        private readonly LRU<GrainId, TValue> cache;

        public LRUBasedGrainDirectoryCache(int maxCacheSize, TimeSpan maxEntryAge)
        {
            cache = new LRU<GrainId, TValue>(maxCacheSize, maxEntryAge, null);
        }

        public void AddOrUpdate(GrainId key, TValue value, int version)
        {
            // ignore the version number
            cache.Add(key, value);
        }

        public bool Remove(GrainId key)
        {
            TValue tmp;
            return cache.RemoveKey(key, out tmp);
        }

        public void Clear()
        {
            cache.Clear();
        }

        public bool LookUp(GrainId key, out TValue result)
        {
            return cache.TryGetValue(key, out result);
        }

        public List<Tuple<GrainId, TValue, int>> KeyValues
        {
            get
            {
                var result = new List<Tuple<GrainId, TValue, int>>();
                IEnumerator<KeyValuePair<GrainId, TValue>> enumerator = cache.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    result.Add(new Tuple<GrainId, TValue, int>(current.Key, current.Value, -1));
                }
                return result;
            }
        }
    }
}
