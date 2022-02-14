
using System;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Configuration
{
    public class GrainDirectoryOptions
    {
        /// <summary>
        /// Configuration type that controls the type of the grain directory caching algorithm that silo use.
        /// </summary>
        public enum CachingStrategyType
        {
            /// <summary>Don't cache.</summary>
            None,
            /// <summary>Standard fixed-size LRU.</summary>
            LRU,
            /// <summary>Adaptive caching with fixed maximum size and refresh. This option should be used in production.</summary>
            Adaptive,
            /// <summary>Custom cache implementation, configured by registering an <see cref="IGrainDirectoryCache"/> implementation in the dependency injection container.</summary>
            Custom
        }

        /// <summary>
        /// Gets or sets the caching strategy to use.
        /// The options are None, which means don't cache directory entries locally;
        /// LRU, which indicates that a standard fixed-size least recently used strategy should be used; and
        /// Adaptive, which indicates that an adaptive strategy with a fixed maximum size should be used.
        /// The Adaptive strategy is used by default.
        /// </summary>
        public CachingStrategyType CachingStrategy { get; set; } = DEFAULT_CACHING_STRATEGY;

        /// <summary>
        /// The default value for <see cref="CachingStrategy"/>.
        /// </summary>
        public const CachingStrategyType DEFAULT_CACHING_STRATEGY = CachingStrategyType.Adaptive;

        /// <summary>
        /// Gets or sets the maximum number of grains to cache directory information for.
        /// </summary>
        public int CacheSize { get; set; } = DEFAULT_CACHE_SIZE;

        /// <summary>
        /// The default value for <see cref="CacheSize"/>.
        /// </summary>
        public const int DEFAULT_CACHE_SIZE = 1000000;

        /// <summary>
        /// Gets or sets the initial (minimum) time, in seconds, to keep a cache entry before revalidating.
        /// </summary>
        public TimeSpan InitialCacheTTL { get; set; } = DEFAULT_INITIAL_CACHE_TTL;

        /// <summary>
        /// The default value for <see cref="InitialCacheTTL"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_INITIAL_CACHE_TTL = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the maximum time, in seconds, to keep a cache entry before revalidating.
        /// </summary>
        public TimeSpan MaximumCacheTTL { get; set; } = DEFAULT_MAXIMUM_CACHE_TTL;

        /// <summary>
        /// The default value for <see cref="MaximumCacheTTL"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_MAXIMUM_CACHE_TTL = TimeSpan.FromSeconds(240);

        /// <summary>
        /// Gets or sets the factor by which cache entry TTLs should be extended when they are found to be stable.
        /// </summary>
        public double CacheTTLExtensionFactor { get; set; } = DEFAULT_TTL_EXTENSION_FACTOR;

        /// <summary>
        /// The default value for <see cref="CacheTTLExtensionFactor"/>.
        /// </summary>
        public const double DEFAULT_TTL_EXTENSION_FACTOR = 2.0;

        /// <summary>
        /// Gets or sets the time span between when we have added an entry for an activation to the grain directory and when we are allowed
        /// to conditionally remove that entry. 
        /// Conditional deregistration is used for lazy clean-up of activations whose prompt deregistration failed for some reason (e.g., message failure).
        /// This should always be at least one minute, since we compare the times on the directory partition, so message delays and clcks skues have
        /// to be allowed.
        /// </summary>
        public TimeSpan LazyDeregistrationDelay { get; set; } = DEFAULT_UNREGISTER_RACE_DELAY;

        /// <summary>
        /// The default value for <see cref="LazyDeregistrationDelay"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_UNREGISTER_RACE_DELAY = TimeSpan.FromMinutes(1);
    }
}
