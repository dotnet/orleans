using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Creates <see cref="IGrainDirectoryCache"/> instances.
    /// </summary>
    public static class GrainDirectoryCacheFactory
    {
        /// <summary>
        /// Creates a new grain directory cache instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="options">The options.</param>
        /// <returns>The newly created <see cref="IGrainDirectoryCache"/> instance.</returns>
        public static IGrainDirectoryCache CreateGrainDirectoryCache(IServiceProvider services, GrainDirectoryOptions options)
        {
            if (options.CacheSize <= 0)
                return new NullGrainDirectoryCache();

            switch (options.CachingStrategy)
            {
                case GrainDirectoryOptions.CachingStrategyType.None:
                    return new NullGrainDirectoryCache();
                case GrainDirectoryOptions.CachingStrategyType.LRU:
                    return new LRUBasedGrainDirectoryCache(options.CacheSize, options.MaximumCacheTTL);
                case GrainDirectoryOptions.CachingStrategyType.Adaptive:
                    return new AdaptiveGrainDirectoryCache(options.InitialCacheTTL, options.MaximumCacheTTL, options.CacheTTLExtensionFactor, options.CacheSize);
                case GrainDirectoryOptions.CachingStrategyType.Custom:
                default:
                    return services.GetRequiredService<IGrainDirectoryCache>();
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
        public void AddOrUpdate(GrainAddress value, int version)
        {
        }

        public bool Remove(GrainId key)
        {
            return false;
        }

        public bool Remove(GrainAddress key)
        {
            return false;
        }

        public void Clear()
        {
        }

        public bool LookUp(GrainId key, out GrainAddress result, out int version)
        {
            result = default;
            version = default;
            return false;
        }

        public IEnumerable<(GrainAddress ActivationAddress, int Version)> KeyValues
        {
            get { yield break; }
        }
    }
}

