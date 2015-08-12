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

using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.GrainDirectory
{
    internal static class GrainDirectoryCacheFactory<TValue>
    {
        internal static IGrainDirectoryCache<TValue> CreateGrainDirectoryCache(GlobalConfiguration cfg)
        {
            if (cfg.CacheSize <= 0)
                return new NullGrainDirectoryCache<TValue>();
            
            switch (cfg.DirectoryCachingStrategy)
            {
                case GlobalConfiguration.DirectoryCachingStrategyType.None:
                    return new NullGrainDirectoryCache<TValue>();
                case GlobalConfiguration.DirectoryCachingStrategyType.LRU:
                    return new LRUBasedGrainDirectoryCache<TValue>(cfg.CacheSize, cfg.MaximumCacheTTL);
                default:
                    return new AdaptiveGrainDirectoryCache<TValue>(cfg.InitialCacheTTL, cfg.MaximumCacheTTL, cfg.CacheTTLExtensionFactor, cfg.CacheSize);
            }
        }

        internal static AsynchAgent CreateGrainDirectoryCacheMaintainer(LocalGrainDirectory router, IGrainDirectoryCache<TValue> cache)
        {
            return cache is AdaptiveGrainDirectoryCache<TValue> ? 
                new AdaptiveDirectoryCacheMaintainer<TValue>(router, cache) : null;
        }
    }

    internal class NullGrainDirectoryCache<TValue> : IGrainDirectoryCache<TValue>
    {
        private static readonly List<Tuple<GrainId, TValue, int>> EmptyList = new List<Tuple<GrainId, TValue, int>>();

        public void AddOrUpdate(GrainId key, TValue value, int version)
        {
        }

        public bool Remove(GrainId key)
        {
            return false;
        }

        public void Clear()
        {
        }

        public bool LookUp(GrainId key, out TValue result)
        {
            result = default(TValue);
            return false;
        }

        public List<Tuple<GrainId, TValue, int>> KeyValues
        {
            get { return EmptyList; }
        }
    }
}

