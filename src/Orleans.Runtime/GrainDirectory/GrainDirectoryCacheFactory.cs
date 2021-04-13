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
        public void AddOrUpdate(ActivationAddress value, int version)
        {
        }

        public bool Remove(GrainId key)
        {
            return false;
        }

        public void Clear()
        {
        }

        public bool LookUp(GrainId key, out ActivationAddress result, out int version)
        {
            result = default;
            version = default;
            return false;
        }

        public IEnumerable<(ActivationAddress ActivationAddress, int Version)> KeyValues
        {
            get { yield break; }
        }
    }
}

