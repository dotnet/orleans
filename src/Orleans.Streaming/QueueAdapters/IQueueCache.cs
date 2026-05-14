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
        /// Notifies the cache that stream registration has started and checkpoints should not advance past this stream
        /// until registration completes.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        void NotifyStreamRegistrationStarted(StreamId streamId) { }

        /// <summary>
        /// Notifies the cache that stream registration has completed.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        void NotifyStreamRegistrationCompleted(StreamId streamId) { }

        /// <summary>
        /// Notifies the cache that a subscription is active on the specified stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="token">The sequence token from which the subscription starts, or <see langword="null"/> if unknown.</param>
        void NotifySubscriptionAdded(StreamId streamId, GuidId subscriptionId, StreamSequenceToken token) { }

        /// <summary>
        /// Notifies the cache that a subscription is no longer active on the specified stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        void NotifySubscriptionRemoved(StreamId streamId, GuidId subscriptionId) { }

        /// <summary>
        /// Notifies the cache that a batch with the given sequence token has been successfully
        /// processed by a specific subscription on the specified stream. Processing can mean either
        /// delivery to the subscription or filtering/skipping by the pulling agent.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="token">The sequence token of the processed batch.</param>
        void NotifyBatchProcessed(StreamId streamId, GuidId subscriptionId, StreamSequenceToken token) { }
    }
}
