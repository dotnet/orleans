using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    [Serializable]
    public class RabbitMQMessage
    {
        /// <summary>
        /// Default constructor for a new RabbitMQ message.
        /// </summary>
        public RabbitMQMessage() { }

        /// <summary>
        /// Builds a new RabbitMQ message.
        /// </summary>
        /// <param name="streamIdentity">Stream Identity</param>
        /// <param name="partitionKey">RabbitMQ partition key for message</param>
        /// <param name="offset">Offset into the RabbitMQ partition where this message was from</param>
        /// <param name="sequenceNumber">Offset into the RabbitMQ partition where this message was from</param>
        /// <param name="dequeueTimeUtc">Time in UTC when this message was read from RabbitMQ into the current service</param>
        /// <param name="properties">User properties from RabbitMQ object</param>
        /// <param name="payload">Binary data from RabbitMQ object</param>
        public RabbitMQMessage(IStreamIdentity streamIdentity,
            string partitionKey,
            string offset,
            long sequenceNumber,
            DateTime dequeueTimeUtc,
            IDictionary<string, object> properties,
            byte[] payload)
        {
            StreamIdentity = streamIdentity;
            PartitionKey = partitionKey;
            Offset = offset;
            SequenceNumber = sequenceNumber;
            DequeueTimeUtc = dequeueTimeUtc;
            Properties = properties;
            Message = payload;
        }

        /// <summary>
        /// Builds a new RabbitMQ message, but through serial encoding.
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="serializationManager"></param>
        public RabbitMQMessage(CachedRabbitMQMessage cachedMessage, SerializationManager serializationManager)
        {
            int readOffset = 0;
            StreamIdentity = new StreamIdentity(cachedMessage.StreamGuid, SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset));
            Offset = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            PartitionKey = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            SequenceNumber = cachedMessage.SequenceNumber;
            DequeueTimeUtc = cachedMessage.DequeueTimeUtc;
            Properties = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset).DeserializeProperties(serializationManager);
            Message = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset).ToArray();
        }

        /// <summary>
        /// Stream identifier
        /// </summary>
        public IStreamIdentity StreamIdentity { get; set; }
        /// <summary>
        /// RabbitMQ partition key
        /// </summary>
        public string PartitionKey { get; set; }
        /// <summary>
        /// Offset into RabbitMQ partition
        /// </summary>
        // todo (mxplusb): rip this out, not useful for RabbitMQ.
        public string Offset { get; }
        /// <summary>
        /// Time message was read from a RabbitMQ stream partition but not the time it was written to the consistent hash exchange.
        /// </summary>
        public long SequenceNumber { get; set; }
        /// <summary>
        /// Time event was read from RabbitMQ and added to cache
        /// </summary>
        public DateTime DequeueTimeUtc { get; set; }
        /// <summary>
        /// User RabbitMQ properties
        /// </summary>
        public IDictionary<string, object> Properties { get; set; }
        /// <summary>
        /// Binary message data
        /// </summary>
        public byte[] Message { get; set; }
    }
}
