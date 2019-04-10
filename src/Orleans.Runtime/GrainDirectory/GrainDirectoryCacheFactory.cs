using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Runtime.GrainDirectory
{
    internal static class GrainDirectoryCacheFactory<TValue>
    {
        internal static IGrainDirectoryCache<TValue> CreateGrainDirectoryCache(GrainDirectoryOptions options)
        {
            if (options.CacheSize <= 0)
                return new NullGrainDirectoryCache<TValue>();
            
            switch (options.CachingStrategy)
            {
                case GrainDirectoryOptions.CachingStrategyType.None:
                    return new NullGrainDirectoryCache<TValue>();
                case GrainDirectoryOptions.CachingStrategyType.LRU:
                    return new LRUBasedGrainDirectoryCache<TValue>(options.CacheSize, options.MaximumCacheTTL);
                default:
                    return new AdaptiveGrainDirectoryCache<TValue>(options.InitialCacheTTL, options.MaximumCacheTTL, options.CacheTTLExtensionFactor, options.CacheSize);
            }
        }

        internal static DedicatedAsynchAgent CreateGrainDirectoryCacheMaintainer(
            ILocalSiloDetails localSiloDetails,
            LocalGrainDirectory localGrainDirectory,
            IGrainDirectoryCache<TValue> cache,
            Func<List<ActivationAddress>, TValue> updateFunc,
            IInternalGrainFactory grainFactory,
            ExecutorService executorService,
            ILoggerFactory loggerFactory)
        {
            var adaptiveCache = cache as AdaptiveGrainDirectoryCache<TValue>;
            if (adaptiveCache is null) return null;
            return new AdaptiveDirectoryCacheMaintainer<TValue>(
                    localSiloDetails,
                    localGrainDirectory,
                    adaptiveCache,
                    updateFunc,
                    grainFactory,
                    executorService,
                    loggerFactory);
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

