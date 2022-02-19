
using System;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// This is a tightly packed cached structure containing a queue message.
    /// It should only contain value types.
    /// </summary>
    public struct CachedMessage
    {
        /// <summary>
        /// Identity of the stream this message is a part of.
        /// </summary>
        public StreamId StreamId;

        /// <summary>
        /// Sequence number. Position of event in queue.
        /// </summary>
        public long SequenceNumber;

        /// <summary>
        /// Event index. Index in batch.
        /// </summary>
        public int EventIndex;

        /// <summary>
        /// Time event was written to the queuing system.
        /// </summary>
        public DateTime EnqueueTimeUtc;

        /// <summary>
        /// Time event was read from the queuing system into this cache.
        /// </summary>
        public DateTime DequeueTimeUtc;

        /// <summary>
        /// Segment containing the serialized event data.
        /// </summary>
        public ArraySegment<byte> Segment;
    }

    /// <summary>
    /// Extensions for <see cref="CachedMessage"/>.
    /// </summary>
    public static class CachedMessageExtensions
    {
        /// <summary>
        /// Compares the specified cached message.
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <param name="token">The token.</param>
        /// <returns>A value indicating the relative order of the token to the cached message.</returns>
        public static int Compare(this ref CachedMessage cachedMessage, StreamSequenceToken token)
        {
            return cachedMessage.SequenceNumber != token.SequenceNumber
                ? (int)(cachedMessage.SequenceNumber - token.SequenceNumber)
                : cachedMessage.EventIndex - token.EventIndex;
        }

        /// <summary>
        /// Compares the stream identifier of a cached message.
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns><see langword="true"/> if streamId is equal to the <see cref="CachedMessage.StreamId"/> value; otherwise <see langword="false"/>.</returns>
        public static bool CompareStreamId(this ref CachedMessage cachedMessage, StreamId streamId) => cachedMessage.StreamId.Equals(streamId);
    }
}
