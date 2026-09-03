using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Provides cached access to messages received from a stream queue.
    /// </summary>
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
        bool TryPurgeFromCache([MaybeNullWhen(false)] out IList<IBatchContainer> purgedItems);

        /// <summary>
        /// Acquire a stream message cursor.  This can be used to retrieve messages from the
        /// cache starting at the location indicated by the provided token.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="token">The token.</param>
        /// <returns>The queue cache cursor.</returns>
        IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token);

        /// <summary>
        /// Acquires a stream message cursor at the specified subscription start position.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="startPosition">The initial subscription position.</param>
        /// <returns>The queue cache cursor.</returns>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="startPosition"/> is not supported by this cache.
        /// </exception>
        IQueueCacheCursor GetCacheCursorAtPosition(StreamId streamId, StreamSubscriptionStartPosition startPosition)
        {
            if (startPosition == StreamSubscriptionStartPosition.Latest)
            {
                return GetCacheCursor(streamId, null);
            }

            if (startPosition != StreamSubscriptionStartPosition.EarliestAvailable)
            {
                throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "The subscription start position is not defined.");
            }

            throw new NotSupportedException(
                $"{GetType().FullName} does not support {StreamSubscriptionStartPosition.EarliestAvailable} cursor positioning.");
        }

        /// <summary>
        /// Returns <see langword="true" /> if this cache is under pressure, <see langword="false" /> otherwise.
        /// </summary>
        /// <returns><see langword="true" /> if this cache is under pressure; otherwise, <see langword="false" />.</returns>
        bool IsUnderPressure();

        /// <summary>
        /// Updates the cache with the current delivery progress of all active subscriptions.
        /// </summary>
        /// <param name="earliestSubscriptionToken">
        /// The earliest last processed sequence token across registered subscriptions.
        /// A <see langword="null"/> value indicates that there are no active subscriptions.
        /// The token is only valid for the duration of the call and must not be stored.
        /// </param>
        /// <param name="utcNow">The current UTC time.</param>
        void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow) { }
    }
}
