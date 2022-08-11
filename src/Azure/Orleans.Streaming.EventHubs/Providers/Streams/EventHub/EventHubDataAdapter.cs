using System;
using System.Collections.Generic;
using Azure.Messaging.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Default event hub data adapter.  Users may subclass to override event data to stream mapping.
    /// </summary>
    public class EventHubDataAdapter : IEventHubDataAdapter
    {
        private readonly Serialization.Serializer serializer;

        /// <summary>
        /// Cache data adapter that adapts EventHub's EventData to CachedEventHubMessage used in cache
        /// </summary>
        public EventHubDataAdapter(Serialization.Serializer serializer)
        {
            this.serializer = serializer;
        }

        /// <summary>
        /// Converts a cached message to a batch container for delivery
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        public virtual IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
        {
            var evenHubMessage = new EventHubMessage(cachedMessage, this.serializer);
            return GetBatchContainer(evenHubMessage);
        }

        /// <summary>
        /// Convert an EventHubMessage to a batch container
        /// </summary>
        /// <param name="eventHubMessage"></param>
        /// <returns></returns>
        protected virtual IBatchContainer GetBatchContainer(EventHubMessage eventHubMessage)
        {
            return new EventHubBatchContainer(eventHubMessage, this.serializer);
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

        public virtual EventData ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null) throw new ArgumentException("EventHub streams currently does not support non-null StreamSequenceToken.", nameof(token));
            return EventHubBatchContainer.ToEventData(this.serializer, streamId, events, requestContext);
        }

        public virtual CachedMessage FromQueueMessage(StreamPosition streamPosition, EventData queueMessage, DateTime dequeueTime, Func<int, ArraySegment<byte>> getSegment)
        {
            return new CachedMessage()
            {
                StreamId = streamPosition.StreamId, 
                SequenceNumber = queueMessage.SequenceNumber,
                EventIndex = streamPosition.SequenceToken.EventIndex,
                EnqueueTimeUtc = queueMessage.EnqueuedTime.UtcDateTime,
                DequeueTimeUtc = dequeueTime,
                Segment = EncodeMessageIntoSegment(queueMessage, getSegment)
            };
        }

        public virtual StreamPosition GetStreamPosition(string partition, EventData queueMessage)
        {
            StreamId streamId = this.GetStreamIdentity(queueMessage);
            StreamSequenceToken token =
                new EventHubSequenceTokenV2(queueMessage.Offset.ToString(), queueMessage.SequenceNumber, 0);
            return new StreamPosition(streamId, token);
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

        /// <summary>
        /// Get the Event Hub partition key to use for a stream.
        /// </summary>
        /// <param name="streamId">The stream Guid.</param>
        /// <returns>The partition key to use for the stream.</returns>
        public virtual string GetPartitionKey(StreamId streamId) => streamId.GetKeyAsString();

        /// <summary>
        /// Get the <see cref="IStreamIdentity"/> for an event message.
        /// </summary>
        /// <param name="queueMessage">The event message.</param>
        /// <returns>The stream identity.</returns>
        public virtual StreamId GetStreamIdentity(EventData queueMessage)
        {
            string streamKey = queueMessage.PartitionKey;
            string streamNamespace = queueMessage.GetStreamNamespaceProperty();
            return StreamId.Create(streamNamespace, streamKey);
        }

        // Placed object message payload into a segment.
        protected virtual ArraySegment<byte> EncodeMessageIntoSegment(EventData queueMessage, Func<int, ArraySegment<byte>> getSegment)
        {
            byte[] propertiesBytes = queueMessage.SerializeProperties(this.serializer);
            var payload = queueMessage.Body.Span;
            var offset = queueMessage.Offset.ToString();
            // get size of namespace, offset, partitionkey, properties, and payload
            int size = SegmentBuilder.CalculateAppendSize(offset) +
                SegmentBuilder.CalculateAppendSize(queueMessage.PartitionKey) +
                SegmentBuilder.CalculateAppendSize(propertiesBytes) +
                SegmentBuilder.CalculateAppendSize(payload);

            // get segment
            ArraySegment<byte> segment = getSegment(size);

            // encode namespace, offset, partitionkey, properties and payload into segment
            int writeOffset = 0;
            SegmentBuilder.Append(segment, ref writeOffset, offset);
            SegmentBuilder.Append(segment, ref writeOffset, queueMessage.PartitionKey);
            SegmentBuilder.Append(segment, ref writeOffset, propertiesBytes);
            SegmentBuilder.Append(segment, ref writeOffset, payload);

            return segment;
        }
    }
}
