using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Replication of EventHub EventData class, reconstructed from cached data CachedEventHubMessage
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class EventHubMessage
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="streamId">Stream Identity</param>
        /// <param name="partitionKey">EventHub partition key for message</param>
        /// <param name="offset">Offset into the EventHub partition where this message was from</param>
        /// <param name="sequenceNumber">Offset into the EventHub partition where this message was from</param>
        /// <param name="enqueueTimeUtc">Time in UTC when this message was injected by EventHub</param>
        /// <param name="dequeueTimeUtc">Time in UTC when this message was read from EventHub into the current service</param>
        /// <param name="properties">User properties from EventData object</param>
        /// <param name="payload">Binary data from EventData object</param>
        public EventHubMessage(StreamId streamId, string partitionKey, string offset, long sequenceNumber,
            DateTime enqueueTimeUtc, DateTime dequeueTimeUtc, IDictionary<string, object> properties, byte[] payload)
        {
            StreamId = streamId;
            PartitionKey = partitionKey;
            Offset = offset;
            SequenceNumber = sequenceNumber;
            EnqueueTimeUtc = enqueueTimeUtc;
            DequeueTimeUtc = dequeueTimeUtc;
            Properties = properties;
            Payload = payload;
        }

        /// <summary>
        /// Duplicate of EventHub's EventData class.
        /// </summary>
        public EventHubMessage(CachedMessage cachedMessage, Serialization.Serializer serializer)
        {
            int readOffset = 0;
            StreamId = cachedMessage.StreamId;
            Offset = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            PartitionKey = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            SequenceNumber = cachedMessage.SequenceNumber;
            EnqueueTimeUtc = cachedMessage.EnqueueTimeUtc;
            DequeueTimeUtc = cachedMessage.DequeueTimeUtc;
            Properties = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset).DeserializeProperties(serializer);
            Payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset).ToArray();
        }

        /// <summary>
        /// Stream identifier
        /// </summary>
        [Id(0)]
        public StreamId StreamId { get; }

        /// <summary>
        /// EventHub partition key
        /// </summary>
        [Id(1)]
        public string PartitionKey { get; }

        /// <summary>
        /// Offset into EventHub partition
        /// </summary>
        [Id(2)]
        public string Offset { get; }

        /// <summary>
        /// Sequence number in EventHub partition
        /// </summary>
        [Id(3)]
        public long SequenceNumber { get; }

        /// <summary>
        /// Time event was written to EventHub
        /// </summary>
        [Id(4)]
        public DateTime EnqueueTimeUtc { get; }

        /// <summary>
        /// Time event was read from EventHub and added to cache
        /// </summary>
        [Id(5)]
        public DateTime DequeueTimeUtc { get; }

        /// <summary>
        /// User EventData properties
        /// </summary>
        [Id(6)]
        public IDictionary<string, object> Properties { get; }

        /// <summary>
        /// Binary event data
        /// </summary>
        [Id(7)]
        public byte[] Payload { get; }
    }
}
