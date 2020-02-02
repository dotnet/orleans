using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Eviction strategy for the PooledQueueCache
    /// </summary>
    public interface IEvictionStrategy
    {
        /// <summary>
        /// IPurgeObservable is implemented by the cache to do purge related actions, and invoked by EvictionStrategy
        /// </summary>
        IPurgeObservable PurgeObservable { set; }

        /// <summary>
        /// Method which will be called when purge is finished
        /// </summary>
        Action<CachedMessage?, CachedMessage?> OnPurged { get; set; }

        /// <summary>
        /// Method which should be called when pulling agent try to do a purge on the cache
        /// </summary>
        /// <param name="utcNow"></param>
        void PerformPurge(DateTime utcNow);

        /// <summary>
        /// Method which should be called when data adapter allocated a new block
        /// </summary>
        /// <param name="newBlock"></param>
        void OnBlockAllocated(FixedSizeBuffer newBlock);
    }

    /// <summary>
    /// IPurgeObservable is implemented by the cache to do purge related actions, and invoked by EvictionStrategy
    /// </summary>
    public interface IPurgeObservable
    {
        /// <summary>
        /// Remove oldest message in the cache
        /// </summary>
        void RemoveOldestMessage();

        /// <summary>
        /// Newest message in the cache
        /// </summary>
        CachedMessage? Newest { get; }

        /// <summary>
        /// Oldest message in the cache
        /// </summary>
        CachedMessage? Oldest { get; }

        /// <summary>
        /// Message count
        /// </summary>
        int ItemCount { get; }

        /// <summary>
        /// Determine if the cache is empty
        /// </summary>
        bool IsEmpty { get; }
    }
}
