
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
        /// Guid of streamId this event is part of
        /// </summary>
        public StreamId StreamId;
        /// <summary>
        /// Sequence number.  Position of event in queue
        /// </summary>
        public long SequenceNumber;
        /// <summary>
        /// Event index.  Index in batch
        /// </summary>
        public int EventIndex;
        /// <summary>
        /// Time event was written to EventHub
        /// </summary>
        public DateTime EnqueueTimeUtc;
        /// <summary>
        /// Time event was read from EventHub into this cache
        /// </summary>
        public DateTime DequeueTimeUtc;
        /// <summary>
        /// Segment containing the serialized event data
        /// </summary>
        public ArraySegment<byte> Segment;
    }

    public static class CachedMessageExtensions
    {
        public static int Compare(this ref CachedMessage cachedMessage, StreamSequenceToken token)
        {
            return cachedMessage.SequenceNumber != token.SequenceNumber
                ? (int)(cachedMessage.SequenceNumber - token.SequenceNumber)
                : cachedMessage.EventIndex - token.EventIndex;
        }

        public static bool CompareStreamId(this ref CachedMessage cachedMessage, StreamId streamId) => cachedMessage.StreamId.Equals(streamId);
    }
}
