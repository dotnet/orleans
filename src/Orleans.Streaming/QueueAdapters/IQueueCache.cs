using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Attempts to retrieve the current delivery progress for a queue cache.
    /// </summary>
    /// <param name="earliestSubscriptionToken">
    /// The earliest last processed sequence token across registered subscriptions,
    /// or <see langword="null"/> when there are no active subscriptions.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if delivery progress was available; otherwise,
    /// <see langword="false"/> when progress is temporarily unavailable.
    /// </returns>
    public delegate bool TryGetDeliveryProgress(out StreamSequenceToken? earliestSubscriptionToken);

    public interface IQueueCache : IQueueFlowController
    {
        /// <summary>
        /// Adds messages to the cache.
        /// </summary>
        /// <param name="messages">The message batches.</param>
        void AddToCache(IList<IBatchContainer> messages);

        /// <summary>
        /// Requests that the cache purge any items that can be purged.
        /// </summary>
        /// <param name="purgedItems">The purged items.</param>
        /// <returns><see langword="true" /> if items were successfully purged from the cache., <see langword="false" /> otherwise.</returns>
        bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems);

        /// <summary>
        /// Acquire a stream message cursor.  This can be used to retrieve messages from the
        /// cache starting at the location indicated by the provided token.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="token">The token.</param>
        /// <returns>The queue cache cursor.</returns>
        IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken token);

        /// <summary>
        /// Returns <see langword="true" /> if this cache is under pressure, <see langword="false" /> otherwise.
        /// </summary>
        /// <returns><see langword="true" /> if this cache is under pressure; otherwise, <see langword="false" />.</returns>
        bool IsUnderPressure();

        /// <summary>
        /// Updates the cache with the current delivery progress of all active subscriptions.
        /// The cache invokes <paramref name="tryGetDeliveryProgress"/> when it needs a current
        /// delivery-progress snapshot to compute a safe checkpoint offset.
        /// </summary>
        /// <param name="tryGetDeliveryProgress">The callback used to retrieve current delivery progress.</param>
        /// <param name="force">
        /// <see langword="true"/> when the cache should retrieve progress even if its normal checkpointing cadence has not elapsed.
        /// </param>
        void UpdateDeliveryProgress(TryGetDeliveryProgress tryGetDeliveryProgress, bool force) { }
    }
}
