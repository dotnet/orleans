using System;
using Microsoft.Azure.EventHubs;
using Orleans.Providers.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    public class EventDataCacheAdapter : IQueueMessageCacheAdapter
    {
        private readonly byte[] propertiesBytes;
        private readonly byte[] body;

        public EventDataCacheAdapter(EventData queueMessage, SerializationManager serializationManager)
            : this(GetStreamPosition(queueMessage),
                   EventHubDataAdapter.OffsetToToken(queueMessage.SystemProperties.Offset),
                   queueMessage.SystemProperties.EnqueuedTimeUtc,
                   queueMessage.SerializeProperties(serializationManager),
                   queueMessage.Body.Array){}

        public EventDataCacheAdapter(StreamPosition streamPosition, byte[] OffsetToken, in DateTime enqueueTimeUtc, byte[] propertiesBytes, byte[] body)
        {
            this.StreamPosition = streamPosition;
            this.OffsetToken = OffsetToken;
            this.EnqueueTimeUtc = enqueueTimeUtc;
            this.propertiesBytes = propertiesBytes;
            this.body = body;
        }

        public int PayloadSize => SegmentBuilder.CalculateAppendSize(this.propertiesBytes) +
                                  SegmentBuilder.CalculateAppendSize(this.body);

        public StreamPosition StreamPosition { get; }

        public byte[] OffsetToken { get; }

        public DateTime EnqueueTimeUtc { get; }

        public void AppendPayload(ArraySegment<byte> segment)
        {
            int writeOffset = 0;
            SegmentBuilder.Append(segment, ref writeOffset, this.propertiesBytes);
            SegmentBuilder.Append(segment, ref writeOffset, this.body);
        }

        protected static StreamPosition GetStreamPosition(EventData queueMessage)
        {
            Guid streamGuid = Guid.Parse(queueMessage.SystemProperties.PartitionKey);
            string streamNamespace = queueMessage.GetStreamNamespaceProperty();
            StreamSequenceToken token = new EventHubSequenceTokenV2(queueMessage.SystemProperties.Offset, queueMessage.SystemProperties.SequenceNumber, 0);
            return new StreamPosition(new StreamIdentity(streamGuid, streamNamespace), token);
        }
    }
}
