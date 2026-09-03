using System;
using System.Collections.Generic;
using Azure.Messaging.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.EventHubs
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
        /// <param name="serializer">The serializer used to encode and decode stream events.</param>
        public EventHubDataAdapter(Serialization.Serializer serializer)
        {
            this.serializer = serializer;
        }

        /// <summary>
        /// Converts a cached message to a batch container for delivery
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <returns>The batch container.</returns>
        public virtual IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
        {
            var evenHubMessage = new EventHubMessage(cachedMessage, this.serializer);
            return GetBatchContainer(evenHubMessage);
        }

        /// <summary>
        /// Convert an EventHubMessage to a batch container
        /// </summary>
        /// <param name="eventHubMessage">The Event Hub message.</param>
        /// <returns>The batch container.</returns>
        protected virtual IBatchContainer GetBatchContainer(EventHubMessage eventHubMessage)
        {
            return new EventHubBatchContainer(eventHubMessage, this.serializer);
        }

        /// <summary>
        /// Gets the stream sequence token from a cached message.
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <returns>The stream sequence token.</returns>
        public virtual StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
        {
            return new EventHubSequenceTokenV2("", cachedMessage.SequenceNumber, 0);
        }

        /// <inheritdoc />
        public virtual EventData ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
        {
            if (token != null) throw new ArgumentException("EventHub streams currently does not support non-null StreamSequenceToken.", nameof(token));
            return EventHubBatchContainer.ToEventData(this.serializer, streamId, events, requestContext);
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public virtual StreamPosition GetStreamPosition(string partition, EventData queueMessage)
        {
            StreamId streamId = this.GetStreamIdentity(queueMessage);
            StreamSequenceToken token =
                new EventHubSequenceTokenV2(queueMessage.OffsetString, queueMessage.SequenceNumber, 0);
            return new StreamPosition(streamId, token);
        }

        /// <summary>
        /// Get offset from cached message.  Left to derived class, as only it knows how to get this from the cached message.
        /// </summary>
        /// <param name="lastItemPurged">The cached message.</param>
        /// <returns>The Event Hub offset.</returns>
        public virtual string GetOffset(CachedMessage lastItemPurged)
        {
            int readOffset = 0;
            return SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset)
                ?? throw new InvalidOperationException("Cached Event Hub message is missing its offset.");
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
            string? streamNamespace = queueMessage.GetStreamNamespaceProperty();
            return StreamId.Create(streamNamespace, streamKey);
        }

        /// <summary>
        /// Encodes an Event Hub message into a cache segment.
        /// </summary>
        /// <param name="queueMessage">The Event Hub message.</param>
        /// <param name="getSegment">The delegate used to allocate a cache segment of the required size.</param>
        /// <returns>The segment containing the encoded message.</returns>
        protected virtual ArraySegment<byte> EncodeMessageIntoSegment(EventData queueMessage, Func<int, ArraySegment<byte>> getSegment)
        {
            byte[] propertiesBytes = queueMessage.SerializeProperties(this.serializer);
            var payload = queueMessage.Body.Span;
            var offset = queueMessage.OffsetString;
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
