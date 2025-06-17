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
#pragma warning disable CS0618 // Type or member is obsolete
                case GrainDirectoryOptions.CachingStrategyType.Adaptive:
#pragma warning restore CS0618 // Type or member is obsolete
                    return new LruGrainDirectoryCache(options.CacheSize);
                case GrainDirectoryOptions.CachingStrategyType.Custom:
                default:
                    return services.GetRequiredService<IGrainDirectoryCache>();
            }
        }

        internal static IGrainDirectoryCache CreateCustomGrainDirectoryCache(IServiceProvider services, GrainDirectoryOptions options)
        {
            var grainDirectoryCache = services.GetService<IGrainDirectoryCache>();
            if (grainDirectoryCache != null)
            {
                return grainDirectoryCache;
            }
            else
            {
                return new LruGrainDirectoryCache(options.CacheSize);
            }
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

