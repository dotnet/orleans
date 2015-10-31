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
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    public class SimpleQueueAdapterCache : IQueueAdapterCache
    {
        private const string CACHE_SIZE_PARAM = "CacheSize";

        private readonly int cacheSize;
        private readonly Logger logger;
        private readonly ConcurrentDictionary<QueueId, IQueueCache> caches;
        
        public SimpleQueueAdapterCache(int cacheSize, Logger logger)
        {
            if (cacheSize <= 0)
                throw new ArgumentOutOfRangeException("cacheSize", "CacheSize must be a positive number.");
            this.cacheSize = cacheSize;
            this.logger = logger;
            caches = new ConcurrentDictionary<QueueId, IQueueCache>();
        }

        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            return caches.AddOrUpdate(queueId, (id) => new SimpleQueueCache(id, cacheSize, logger), (id, queueCache) => queueCache);
        }

        public int Size
        {
            get { return caches.Select(pair => pair.Value.Size).Sum(); }
        }

        public static int ParseSize(ReadOnlyDictionary<string, string> config, int defaultSize)
        {
            string cacheSizeString;
            int cacheSize = defaultSize;
            if (config.TryGetValue(CACHE_SIZE_PARAM, out cacheSizeString))
            {
                if (!int.TryParse(cacheSizeString, out cacheSize))
                    throw new ArgumentException(String.Format("{0} invalid.  Must be int", CACHE_SIZE_PARAM));
            }
            return cacheSize;
        }
    }
}
