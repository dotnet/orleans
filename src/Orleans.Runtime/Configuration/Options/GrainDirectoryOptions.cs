
using System;

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
            Adaptive
        }

        /// <summary>
        /// The DirectoryCachingStrategy attribute specifies the caching strategy to use.
        /// The options are None, which means don't cache directory entries locally;
        /// LRU, which indicates that a standard fixed-size least recently used strategy should be used; and
        /// Adaptive, which indicates that an adaptive strategy with a fixed maximum size should be used.
        /// The Adaptive strategy is used by default.
        /// </summary>
        public CachingStrategyType CachingStrategy { get; set; } = DEFAULT_CACHING_STRATEGY;
        public const CachingStrategyType DEFAULT_CACHING_STRATEGY = CachingStrategyType.Adaptive;

        /// <summary>
        /// The CacheSize attribute specifies the maximum number of grains to cache directory information for.
        /// </summary>
        public int CacheSize { get; set; } = DEFAULT_CACHE_SIZE;
        public const int DEFAULT_CACHE_SIZE = 1000000;

        /// <summary>
        /// The InitialTTL attribute specifies the initial (minimum) time, in seconds, to keep a cache entry before revalidating.
        /// </summary>
        public TimeSpan InitialCacheTTL { get; set; } = DEFAULT_INITIAL_CACHE_TTL;
        public static readonly TimeSpan DEFAULT_INITIAL_CACHE_TTL = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The MaximumTTL attribute specifies the maximum time, in seconds, to keep a cache entry before revalidating.
        /// </summary>
        public TimeSpan MaximumCacheTTL { get; set; } = DEFAULT_MAXIMUM_CACHE_TTL;
        public static readonly TimeSpan DEFAULT_MAXIMUM_CACHE_TTL = TimeSpan.FromSeconds(240);

        /// <summary>
        /// The TTLExtensionFactor attribute specifies the factor by which cache entry TTLs should be extended when they are found to be stable.
        /// </summary>
        public double CacheTTLExtensionFactor { get; set; } = DEFAULT_TTL_EXTENSION_FACTOR;
        public const double DEFAULT_TTL_EXTENSION_FACTOR = 2.0;

        /// <summary>
        /// The time span between when we have added an entry for an activation to the grain directory and when we are allowed
        /// to conditionally remove that entry. 
        /// Conditional deregistration is used for lazy clean-up of activations whose prompt deregistration failed for some reason (e.g., message failure).
        /// This should always be at least one minute, since we compare the times on the directory partition, so message delays and clcks skues have
        /// to be allowed.
        /// </summary>
        public TimeSpan LazyDeregistrationDelay { get; set; } = DEFAULT_UNREGISTER_RACE_DELAY;
        public static readonly TimeSpan DEFAULT_UNREGISTER_RACE_DELAY = TimeSpan.FromMinutes(1);
    }
}
