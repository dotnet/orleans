using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Runtime.GrainDirectory
{
    internal static class GrainDirectoryCacheFactory
    {
        internal static IGrainDirectoryCache CreateGrainDirectoryCache(GrainDirectoryOptions options)
        {
            if (options.CacheSize <= 0)
                return new NullGrainDirectoryCache();
            
            switch (options.CachingStrategy)
            {
                case GrainDirectoryOptions.CachingStrategyType.None:
                    return new NullGrainDirectoryCache();
                case GrainDirectoryOptions.CachingStrategyType.LRU:
                    return new LRUBasedGrainDirectoryCache(options.CacheSize, options.MaximumCacheTTL);
                default:
                    return new AdaptiveGrainDirectoryCache(options.InitialCacheTTL, options.MaximumCacheTTL, options.CacheTTLExtensionFactor, options.CacheSize);
            }
        }

        internal static AdaptiveDirectoryCacheMaintainer CreateGrainDirectoryCacheMaintainer(
            LocalGrainDirectory router,
            IGrainDirectoryCache cache,
            IInternalGrainFactory grainFactory,
            ILoggerFactory loggerFactory)
        {
            var adaptiveCache = cache as AdaptiveGrainDirectoryCache;
            return adaptiveCache != null
                ? new AdaptiveDirectoryCacheMaintainer(router, adaptiveCache, grainFactory, loggerFactory)
                : null;
        }
    }

    internal class NullGrainDirectoryCache : IGrainDirectoryCache
    {
        private static readonly List<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> EmptyList = new List<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>>();

        public void AddOrUpdate(GrainId key, IReadOnlyList<Tuple<SiloAddress, ActivationId>> value, int version)
        {
        }

        public bool Remove(GrainId key)
        {
            return false;
        }

        public void Clear()
        {
        }

        public bool LookUp(GrainId key, out IReadOnlyList<Tuple<SiloAddress, ActivationId>> result, out int version)
        {
            result = default(IReadOnlyList<Tuple<SiloAddress, ActivationId>>);
            version = default(int);
            return false;
        }

        public IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> KeyValues
        {
            get { return EmptyList; }
        }
    }
}

