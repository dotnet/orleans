using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Eviction strategy for the PooledQueueCache
    /// </summary>
    public interface IEvictionStrategy
    {
        /// <summary>
        /// Gets the <see cref="IPurgeObservable"/>, which is implemented by the cache to do purge related actions and invoked by the eviction strategy.
        /// </summary>
        IPurgeObservable PurgeObservable { set; }

        /// <summary>
        /// Gets or sets the method which will be called when purge is finished.
        /// </summary>
        Action<CachedMessage?, CachedMessage?> OnPurged { get; set; }

        /// <summary>
        /// Method which should be called when pulling agent try to do a purge on the cache
        /// </summary>
        /// <param name="utcNow">The current time (UTC)</param>
        void PerformPurge(DateTime utcNow);

        /// <summary>
        /// Method which should be called when data adapter allocated a new block
        /// </summary>
        /// <param name="newBlock">The new block.</param>
        void OnBlockAllocated(FixedSizeBuffer newBlock);
    }

    /// <summary>
    /// Functionality for purge-related actions.
    /// </summary>
    public interface IPurgeObservable
    {
        /// <summary>
        /// Removes oldest message in the cache.
        /// </summary>
        void RemoveOldestMessage();

        /// <summary>
        /// Gets the newest message in the cache.
        /// </summary>
        CachedMessage? Newest { get; }

        /// <summary>
        /// Gets the oldest message in the cache.
        /// </summary>
        CachedMessage? Oldest { get; }

        /// <summary>
        /// Gets the message count.
        /// </summary>
        int ItemCount { get; }

        /// <summary>
        /// Gets a value indicating whether this instance is empty.
        /// </summary>
        bool IsEmpty { get; }
    }
}
