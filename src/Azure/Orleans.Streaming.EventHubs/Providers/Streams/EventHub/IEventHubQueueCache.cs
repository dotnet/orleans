using System;
using Orleans.Streams;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.EventHubs;
using Orleans.Runtime;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Interface for a stream message cache that stores EventHub EventData
    /// </summary>
    public interface IEventHubQueueCache : IQueueFlowController, IDisposable
    {
        /// <summary>
        /// Add a list of EventHub EventData to the cache.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        List<StreamPosition> Add(List<EventData> message, DateTime dequeueTimeUtc);

        /// <summary>
        /// Get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="sequenceToken">The position from which to begin reading.</param>
        /// <returns>The acquired cache cursor.</returns>
        /// <exception cref="QueueCacheMissException">
        /// The requested token is older than the messages retained by the cache.
        /// </exception>
        [Obsolete("Use TryGetCursor instead.")]
        object GetCursor(StreamId streamId, StreamSequenceToken? sequenceToken);

        /// <summary>
        /// Attempts to get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="sequenceToken">The position from which to begin reading.</param>
        /// <returns>
        /// A successful result containing the acquired cursor, or a cache-miss result containing the
        /// unavailable position and current cache bounds.
        /// </returns>
        QueueCacheCursorResult<object> TryGetCursor(StreamId streamId, StreamSequenceToken? sequenceToken)
        {
            try
            {
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy method.
                return QueueCacheCursorResult<object>.FromCursor(GetCursor(streamId, sequenceToken));
#pragma warning restore CS0618
            }
            catch (QueueCacheMissException exception)
            {
                return QueueCacheCursorResult<object>.FromCacheMiss(
                    new(exception.Requested, exception.Low, exception.High));
            }
        }

        /// <summary>
        /// Attempts to get a cursor into the cache at the specified subscription start position.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="startPosition">The initial subscription position.</param>
        /// <returns>
        /// A successful result containing the acquired cursor, a cache-miss result containing the unavailable
        /// position and current cache bounds, or <see cref="QueueCacheCursorResultKind.NotSupported"/>.
        /// </returns>
        /// <remarks>
        /// The default implementation acquires the latest cursor using <see cref="TryGetCursor"/>
        /// with a null token. Providers support earliest positioning by implementing this method
        /// and returning a cursor positioned inclusively at the oldest retained message for the stream.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startPosition"/> is not defined.</exception>
        QueueCacheCursorResult<object> TryGetCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
        {
            if (startPosition == StreamSubscriptionStartPosition.Latest)
            {
                return TryGetCursor(streamId, null);
            }

            if (startPosition != StreamSubscriptionStartPosition.EarliestAvailable)
            {
                throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "The subscription start position is not defined.");
            }

            return QueueCacheCursorResult<object>.NotSupported;
        }

        /// <summary>
        /// Refreshes an inactive cursor at the provided sequence token.
        /// </summary>
        /// <param name="cursor">The cursor to refresh.</param>
        /// <param name="sequenceToken">The sequence token to position the cursor at.</param>
        void Refresh(object cursor, StreamSequenceToken? sequenceToken) { }

        /// <summary>
        /// Try to get the next message in the cache for the provided cursor.
        /// </summary>
        /// <param name="cursorObj">The cache cursor.</param>
        /// <param name="message">The next message when one is available.</param>
        /// <returns><see langword="true"/> when a message was returned; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="QueueCacheMissException">
        /// The cursor position is older than the messages retained by the cache.
        /// </exception>
        [Obsolete("Use TryGetNextMessageWithResult instead.")]
        bool TryGetNextMessage(object cursorObj, [NotNullWhen(true)] out IBatchContainer? message);

        /// <summary>
        /// Attempts to get the next message in the cache for the provided cursor.
        /// </summary>
        /// <param name="cursorObj">The cache cursor.</param>
        /// <param name="message">The next message when one is available.</param>
        /// <returns>
        /// A successful result with a non-null <paramref name="message"/>, <see cref="QueueCacheCursorMoveResultKind.NoData"/>
        /// with a null message, or a cache-miss result with a null message.
        /// </returns>
        QueueCacheCursorMoveResult TryGetNextMessageWithResult(object cursorObj, out IBatchContainer? message)
        {
            try
            {
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy method.
                if (TryGetNextMessage(cursorObj, out message))
                {
                    return QueueCacheCursorMoveResult.Success;
                }
#pragma warning restore CS0618

                message = null;
                return QueueCacheCursorMoveResult.NoData;
            }
            catch (QueueCacheMissException exception)
            {
                message = null;
                return QueueCacheCursorMoveResult.FromCacheMiss(
                    new(exception.Requested, exception.Low, exception.High));
            }
        }

        /// <summary>
        /// Add cache pressure monitor to the cache's back pressure algorithm
        /// </summary>
        /// <param name="monitor"></param>
        void AddCachePressureMonitor(ICachePressureMonitor monitor);

        /// <summary>
        /// Send purge signal to the cache, the cache will perform a time based purge on its cached messages
        /// </summary>
        void SignalPurge();
    }
}
