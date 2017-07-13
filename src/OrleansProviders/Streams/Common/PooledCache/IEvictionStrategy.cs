using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Eviction strategy for the PooledQueueCache
    /// </summary>
    public interface IEvictionStrategy<TCachedMessage>
        where TCachedMessage : struct
    {
        /// <summary>
        /// IPurgeObservable is implemented by the cache to do purge related actions, and invoked by EvictionStrategy
        /// </summary>
        IPurgeObservable<TCachedMessage> PurgeObservable { set; }

        /// <summary>
        /// Method which will be called when purge is finished
        /// </summary>
        Action<TCachedMessage?, TCachedMessage?> OnPurged { get; set; }

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

    public static class EvictionStrategyCommonUtils
    {
        public static void WireUpEvictionStrategy<TQueueMessage, TCachedMessage>(PooledQueueCache<TQueueMessage, TCachedMessage> cache,
            ICacheDataAdapter<TQueueMessage, TCachedMessage> cacheDataAdapter, IEvictionStrategy<TCachedMessage> evictionStrategy)
            where TCachedMessage : struct
        {
            evictionStrategy.PurgeObservable = cache;
            cacheDataAdapter.OnBlockAllocated = evictionStrategy.OnBlockAllocated;
        }
    }

    /// <summary>
    /// IPurgeObservable is implemented by the cache to do purge related actions, and invoked by EvictionStrategy
    /// </summary>
    /// <typeparam name="TCachedMessage"></typeparam>
    public interface IPurgeObservable<TCachedMessage>
         where TCachedMessage : struct
    {
        /// <summary>
        /// Remove oldest message in the cache
        /// </summary>
        void RemoveOldestMessage();

        /// <summary>
        /// Newest message in the cache
        /// </summary>
        TCachedMessage? Newest { get; }

        /// <summary>
        /// Oldest message in the cache
        /// </summary>
        TCachedMessage? Oldest { get; }

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
