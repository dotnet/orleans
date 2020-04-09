using System;
using System.Collections.Generic;
using Microsoft.Azure.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Default event hub data adapter.  Users may subclass to override event data to stream mapping.
    /// </summary>
    public class EventHubDataAdapter : IEventHubDataAdapter
    {
        private readonly SerializationManager serializationManager;

        /// <summary>
        /// Cache data adapter that adapts EventHub's EventData to CachedEventHubMessage used in cache
        /// </summary>
        /// <param name="serializationManager"></param>
        public EventHubDataAdapter(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        /// <summary>
        /// Converts a cached message to a batch container for delivery
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        public virtual IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
        {
            var evenHubMessage = new EventHubMessage(cachedMessage, this.serializationManager);
            return GetBatchContainer(evenHubMessage);
        }

        /// <summary>
        /// Convert an EventHubMessage to a batch container
        /// </summary>
        /// <param name="eventHubMessage"></param>
        /// <returns></returns>
        protected virtual IBatchContainer GetBatchContainer(EventHubMessage eventHubMessage)
        {
            return new EventHubBatchContainer(eventHubMessage, this.serializationManager);
        }

        /// <summary>
        /// Gets the stream sequence token from a cached message.
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        public virtual StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
        {
            return new EventHubSequenceTokenV2("", cachedMessage.SequenceNumber, 0);
        }

        public virtual EventData ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null) throw new ArgumentException("EventHub streams currently does not support non-null StreamSequenceToken.", nameof(token));
            return EventHubBatchContainer.ToEventData(this.serializationManager, streamGuid, streamNamespace, events, requestContext);
        }

        public virtual CachedMessage FromQueueMessage(StreamPosition streamPosition, EventData queueMessage, DateTime dequeueTime, Func<int, ArraySegment<byte>> getSegment)
        {
            return new CachedMessage()
            {
                StreamGuid = streamPosition.StreamIdentity.Guid,
                StreamNamespace = streamPosition.StreamIdentity.Namespace != null ? string.Intern(streamPosition.StreamIdentity.Namespace) : null,
                SequenceNumber = queueMessage.SystemProperties.SequenceNumber,
                EventIndex = streamPosition.SequenceToken.EventIndex,
                EnqueueTimeUtc = queueMessage.SystemProperties.EnqueuedTimeUtc,
                DequeueTimeUtc = dequeueTime,
                Segment = EncodeMessageIntoSegment(queueMessage, getSegment)
            };
        }

        public virtual StreamPosition GetStreamPosition(string partition, EventData queueMessage)
        {
            Guid streamGuid =
            Guid.Parse(queueMessage.SystemProperties.PartitionKey);
            string streamNamespace = queueMessage.GetStreamNamespaceProperty();
            IStreamIdentity stremIdentity = new StreamIdentity(streamGuid, streamNamespace);
            StreamSequenceToken token =
                new EventHubSequenceTokenV2(queueMessage.SystemProperties.Offset, queueMessage.SystemProperties.SequenceNumber, 0);
            return new StreamPosition(stremIdentity, token);
        }

        /// <summary>
        /// Get offset from cached message.  Left to derived class, as only it knows how to get this from the cached message.
        /// </summary>
        public virtual string GetOffset(CachedMessage lastItemPurged)
        {
            // TODO figure out how to get this from the adapter
            int readOffset = 0;
            return SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read offset
        }

        // Placed object message payload into a segment.
        protected virtual ArraySegment<byte> EncodeMessageIntoSegment(EventData queueMessage, Func<int, ArraySegment<byte>> getSegment)
        {
            byte[] propertiesBytes = queueMessage.SerializeProperties(this.serializationManager);
            ArraySegment<byte> payload = queueMessage.Body;
            // get size of namespace, offset, partitionkey, properties, and payload
            int size = SegmentBuilder.CalculateAppendSize(queueMessage.SystemProperties.Offset) +
                SegmentBuilder.CalculateAppendSize(queueMessage.SystemProperties.PartitionKey) +
                SegmentBuilder.CalculateAppendSize(propertiesBytes) +
                SegmentBuilder.CalculateAppendSize(payload);

            // get segment
            ArraySegment<byte> segment = getSegment(size);

            // encode namespace, offset, partitionkey, properties and payload into segment
            int writeOffset = 0;
            SegmentBuilder.Append(segment, ref writeOffset, queueMessage.SystemProperties.Offset);
            SegmentBuilder.Append(segment, ref writeOffset, queueMessage.SystemProperties.PartitionKey);
            SegmentBuilder.Append(segment, ref writeOffset, propertiesBytes);
            SegmentBuilder.Append(segment, ref writeOffset, payload);

            return segment;
        }
    }
}
