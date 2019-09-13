using System;
using Orleans.Providers.Streams.Common;

namespace Orleans.Providers.Abstractions
{
    /// <summary>
    /// FiFo Eviction strategy
    /// </summary>
    public interface IFiFoEvictionStrategy<TCachedItem>
        where TCachedItem : struct
    {
        /// <summary>
        /// Attempt to evict items from cache.
        /// </summary>
        bool TryEvict(IFiFoEvictableCache<TCachedItem> cache, in DateTime utcNow);

        /// <summary>
        /// Hack - Eviction strategy cleans up pooled resources so it needs know what blocks are in use
        /// TODO - jbragg find cleaner way of managing pooled resources.
        /// </summary>
        /// <param name="newBlock"></param>
        void OnBlockAllocated(FixedSizeBuffer newBlock);
    }
}
