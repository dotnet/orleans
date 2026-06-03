using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{
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
        /// Called periodically by the pulling agent so the cache can compute a safe checkpoint
        /// offset (e.g., the low watermark across all subscriptions).
        /// </summary>
        /// <param name="earliestSubscriptionToken">
        /// The earliest last processed sequence token across registered subscriptions.
        /// A <see langword="null"/> value indicates that there are no active subscriptions.
        /// The token is only valid for the duration of the call and must not be stored.
        /// </param>
        /// <param name="hasPendingRegistrations">
        /// <see langword="true"/> if any stream registration is still in progress,
        /// meaning the full set of subscriptions is not yet known and checkpoints should not advance.
        /// </param>
        void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, bool hasPendingRegistrations) { }
    }
}
