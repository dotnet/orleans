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
        /// <exception cref="QueueCacheMissException">
        /// The requested token is older than the messages retained by the cache.
        /// </exception>
        [Obsolete("Use TryGetCacheCursor instead.")]
        IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token);

        /// <summary>
        /// Attempts to acquire a stream message cursor at the location indicated by the provided token.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="token">The token.</param>
        /// <returns>
        /// A successful result containing the acquired cursor, or a cache-miss result containing the
        /// unavailable position and current cache bounds.
        /// </returns>
        QueueCacheCursorResult<IQueueCacheCursor> TryGetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        {
            try
            {
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy method.
                return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(GetCacheCursor(streamId, token));
#pragma warning restore CS0618
            }
            catch (QueueCacheMissException exception)
            {
                return QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(
                    new(exception.Requested, exception.Low, exception.High));
            }
        }

        /// <summary>
        /// Attempts to acquire a stream message cursor at the specified subscription start position.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="startPosition">The initial subscription position.</param>
        /// <returns>
        /// A successful result containing the acquired cursor, a cache-miss result containing the unavailable
        /// position and current cache bounds, or <see cref="QueueCacheCursorResultKind.NotSupported"/>.
        /// </returns>
        /// <remarks>
        /// The default implementation adapts <see cref="GetCacheCursorAtPosition"/> so that existing
        /// providers retain their positioning behavior. Providers implementing this method directly
        /// should also implement the obsolete positioning method for legacy callers.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startPosition"/> is not defined.</exception>
        QueueCacheCursorResult<IQueueCacheCursor> TryGetCacheCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
        {
            try
            {
#pragma warning disable CS0618 // Preserve positioning implemented by existing providers.
                return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(GetCacheCursorAtPosition(streamId, startPosition));
#pragma warning restore CS0618
            }
            catch (QueueCacheMissException exception)
            {
                return QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(
                    new(exception.Requested, exception.Low, exception.High));
            }
            catch (NotSupportedException)
            {
                return QueueCacheCursorResult<IQueueCacheCursor>.NotSupported;
            }
        }

        /// <summary>
        /// Acquires a stream message cursor at the specified subscription start position.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="startPosition">The initial subscription position.</param>
        /// <returns>The queue cache cursor.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startPosition"/> is not defined.</exception>
        /// <exception cref="QueueCacheMissException">
        /// The requested position is older than the messages retained by the cache.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The cache does not support <paramref name="startPosition"/>.
        /// </exception>
        [Obsolete("Use TryGetCacheCursorAtPosition instead.")]
        IQueueCacheCursor GetCacheCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
        {
            if (startPosition == StreamSubscriptionStartPosition.Latest)
            {
#pragma warning disable CS0618 // Preserve the exact legacy exception and cursor behavior.
                return GetCacheCursor(streamId, null);
#pragma warning restore CS0618
            }

            if (startPosition != StreamSubscriptionStartPosition.EarliestAvailable)
            {
                throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "The subscription start position is not defined.");
            }

            throw new NotSupportedException(
                $"{GetType().FullName} does not support {startPosition} cursor positioning.");
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
