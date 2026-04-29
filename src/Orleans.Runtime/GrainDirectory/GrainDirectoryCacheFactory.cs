using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

#nullable disable
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
            => CreateGrainDirectoryCache(services, options, out _);

        internal static IGrainDirectoryCache CreateGrainDirectoryCache(IServiceProvider services, GrainDirectoryOptions options, out bool disposeCache)
        {
            if (options.CacheSize <= 0)
            {
                disposeCache = true;
                return new NullGrainDirectoryCache();
            }

            switch (options.CachingStrategy)
            {
                case GrainDirectoryOptions.CachingStrategyType.None:
                    disposeCache = true;
                    return new NullGrainDirectoryCache();
                case GrainDirectoryOptions.CachingStrategyType.LRU:
#pragma warning disable CS0618 // Type or member is obsolete
                case GrainDirectoryOptions.CachingStrategyType.Adaptive:
#pragma warning restore CS0618 // Type or member is obsolete
                    disposeCache = true;
                    return CreateLruGrainDirectoryCache(services, options);
                case GrainDirectoryOptions.CachingStrategyType.Custom:
                default:
                    disposeCache = false;
                    return services.GetRequiredService<IGrainDirectoryCache>();
            }
        }

        internal static IGrainDirectoryCache CreateCustomGrainDirectoryCache(IServiceProvider services, GrainDirectoryOptions options)
            => CreateCustomGrainDirectoryCache(services, options, out _);

        internal static IGrainDirectoryCache CreateCustomGrainDirectoryCache(IServiceProvider services, GrainDirectoryOptions options, out bool disposeCache)
        {
            var grainDirectoryCache = services.GetService<IGrainDirectoryCache>();
            if (grainDirectoryCache is not null)
            {
                disposeCache = false;
                return grainDirectoryCache;
            }

            disposeCache = true;
            return CreateLruGrainDirectoryCache(services, options);
        }

        internal static ValueTask DisposeGrainDirectoryCacheAsync(IGrainDirectoryCache cache)
        {
            switch (cache)
            {
                case IAsyncDisposable asyncDisposable:
                    return asyncDisposable.DisposeAsync();
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            return default;
        }

        private static IGrainDirectoryCache CreateLruGrainDirectoryCache(IServiceProvider services, GrainDirectoryOptions options)
        {
            var timeProvider = services?.GetService<TimeProvider>() ?? TimeProvider.System;
#pragma warning disable CS0618 // Type or member is obsolete
            return new LruGrainDirectoryCache(options.CacheSize, options.MaximumCacheTTL, timeProvider);
#pragma warning restore CS0618 // Type or member is obsolete
        }
    }

    internal sealed class NullGrainDirectoryCache : IGrainDirectoryCache
    {
        public void AddOrUpdate(GrainAddress value, int version)
        {
        }

        public bool Remove(GrainId key) => false;

        public bool Remove(GrainAddress key) => false;

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

