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

        internal static AsynchAgent CreateGrainDirectoryCacheMaintainer(
            LocalGrainDirectory router,
            IGrainDirectoryCache<TValue> cache,
            Func<List<ActivationAddress>, TValue> updateFunc)
        {
            var adaptiveCache = cache as AdaptiveGrainDirectoryCache<TValue>;
            return adaptiveCache != null
                ? new AdaptiveDirectoryCacheMaintainer<TValue>(router, adaptiveCache, updateFunc)
                : null;
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

        public bool LookUp(GrainId key, out TValue result, out int version)
        {
            result = default(TValue);
            version = default(int);
            return false;
        }

        public IReadOnlyList<Tuple<GrainId, TValue, int>> KeyValues
        {
            get { return EmptyList; }
        }
    }
}

