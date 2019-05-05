using System;

namespace Orleans.RabbitMQ.Providers
{
    public struct CachedRabbitMQMessage
    {
        /// <summary>
        /// Guid of streamId this event is part of
        /// </summary>
        public Guid StreamGuid;
        /// <summary>
        /// Time message was read from a RabbitMQ stream partition but not the time it was written to the consistent hash exchange.
        /// </summary>
        public long SequenceNumber;
        /// <summary>
        /// Segment containing the serialized event data
        /// </summary>
        public ArraySegment<byte> Segment;
        /// <summary>
        /// Time event was read from RabbitMQ into this cache.
        /// </summary>
        public DateTime DequeueTimeUtc;
    }
}