using System;
using System.Collections.Generic;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Providers.Streams.Common;
using System.Linq;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Replication of EventHub EventData class, reconstructed from cached data CachedEventHubMessage
    /// </summary>
    [Serializable]
    public class EventHubMessage
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="streamIdentity">Stream Identity</param>
        /// <param name="partitionKey">EventHub partition key for message</param>
        /// <param name="offset">Offset into the EventHub partition where this message was from</param>
        /// <param name="sequenceNumber">Offset into the EventHub partition where this message was from</param>
        /// <param name="enqueueTimeUtc">Time in UTC when this message was injected by EventHub</param>
        /// <param name="dequeueTimeUtc">Time in UTC when this message was read from EventHub into the current service</param>
        /// <param name="properties">User properties from EventData object</param>
        /// <param name="payload">Binary data from EventData object</param>
        public EventHubMessage(IStreamIdentity streamIdentity, string partitionKey, string offset, long sequenceNumber,
            DateTime enqueueTimeUtc, DateTime dequeueTimeUtc, IDictionary<string, object> properties, byte[] payload)
        {
            StreamIdentity = streamIdentity;
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
        /// NOTE: Tightly coupled with default EventDataCacheAdapter
        /// TODO: Depricate, can't remove without breaking backwards compatability, but should depricate. - jbragg
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="serializationManager"></param>
        public EventHubMessage(CachedMessage cachedMessage, SerializationManager serializationManager)
        {
            StreamIdentityToken streamIdentityToken = new StreamIdentityToken(cachedMessage.StreamIdToken().ToArray());
            EventSequenceToken.Parse(cachedMessage.SequenceToken().ToArray(), out long sequenceNumber, out int ignore);
            ArraySegment<byte> payload = cachedMessage.Payload();
            int readOffset = 0;

            this.StreamIdentity = new StreamIdentity(streamIdentityToken.Guid, streamIdentityToken.Namespace);
            this.Offset = EventHubDataAdapter.TokenToOffset(cachedMessage.OffsetToken().ToArray());
            this.PartitionKey = streamIdentityToken.Guid.ToString();
            this.SequenceNumber = sequenceNumber;
            this.EnqueueTimeUtc = cachedMessage.EnqueueTimeUtc;
            this.DequeueTimeUtc = cachedMessage.DequeueTimeUtc;
            this.Properties = SegmentBuilder.ReadNextBytes(payload, ref readOffset).DeserializeProperties(serializationManager);
            this.Payload = SegmentBuilder.ReadNextBytes(payload, ref readOffset).ToArray();
        }

        /// <summary>
        /// Stream identifier
        /// </summary>
        public IStreamIdentity StreamIdentity { get; }
        /// <summary>
        /// EventHub partition key
        /// </summary>
        public string PartitionKey { get; }
        /// <summary>
        /// Offset into EventHub partition
        /// </summary>
        public string Offset { get; }
        /// <summary>
        /// Sequence number in EventHub partition
        /// </summary>
        public long SequenceNumber { get; }
        /// <summary>
        /// Time event was written to EventHub
        /// </summary>
        public DateTime EnqueueTimeUtc { get; }
        /// <summary>
        /// Time event was read from EventHub and added to cache
        /// </summary>
        public DateTime DequeueTimeUtc { get; }
        /// <summary>
        /// User EventData properties
        /// </summary>
        public IDictionary<string, object> Properties { get; }
        /// <summary>
        /// Binary event data
        /// </summary>
        public byte[] Payload { get; }
    }
}
