
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// This is a tightly packed cached structure containing an event hub message.  
    /// It should only contain value types.
    /// </summary>
    public struct CachedEventHubMessage
    {
        /// <summary>
        /// Guid of streamId this event is part of
        /// </summary>
        public Guid StreamGuid;
        /// <summary>
        /// EventHub sequence number.  Position of event in partition
        /// </summary>
        public long SequenceNumber;
        /// <summary>
        /// Time event was writen to EventHub
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

    /// <summary>
    /// Replication of EventHub EventData class, reconstructed from cached data CachedEventHubMessage
    /// </summary>
    [Serializable]
    public class EventHubMessage
    {
        /// <summary>
        /// Contructor
        /// </summary>
        /// <param name="streamIdentity">Stream Identity</param>
        /// <param name="partitionKey">EventHub partition key for message</param>
        /// <param name="offset">Offset into the EventHub parition where this message was from</param>
        /// <param name="sequenceNumber">Offset into the EventHub parition where this message was from</param>
        /// <param name="enqueueTimeUtc">Time in UTC when this message was injected by EventHub</param>
        /// <param name="dequeueTimeUtc">Time in UTC when this message was read from EventHub into the current service</param>
        /// <param name="properties">User properties from EventData object</param>
        /// <param name="payload">Binary data from EventData objbect</param>
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
        /// </summary>
        /// <param name="cachedMessage"></param>
        public EventHubMessage(CachedEventHubMessage cachedMessage)
        {
            int readOffset = 0;
            StreamIdentity = new StreamIdentity(cachedMessage.StreamGuid, SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset));
            Offset = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            PartitionKey = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            SequenceNumber = cachedMessage.SequenceNumber;
            EnqueueTimeUtc = cachedMessage.EnqueueTimeUtc;
            DequeueTimeUtc = cachedMessage.DequeueTimeUtc;
            Properties = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset).DeserializeProperties();
            Payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset).ToArray();
        }

        /// <summary>
        /// Stream identifer
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

    /// <summary>
    /// Default eventhub data comparer.  Implements comparisions against CachedEventHubMessage
    /// </summary>
    public class EventHubDataComparer : ICacheDataComparer<CachedEventHubMessage>
    {
        /// <summary>
        /// Singleton instance, since type is stateless using this will reduce allocations.
        /// </summary>
        public static readonly ICacheDataComparer<CachedEventHubMessage> Instance = new EventHubDataComparer();

        /// <summary>
        /// Compare a cached message with a sequence token to determine if it message is before or after the token
        /// </summary>
        public int Compare(CachedEventHubMessage cachedMessage, StreamSequenceToken streamToken)
        {
            var realToken = (EventSequenceToken)streamToken;
            return (int)(cachedMessage.SequenceNumber - realToken.SequenceNumber);
        }

        /// <summary>
        /// Checks to see if the cached message is part of the provided stream
        /// </summary>
        public bool Equals(CachedEventHubMessage cachedMessage, IStreamIdentity streamIdentity)
        {
            int result = cachedMessage.StreamGuid.CompareTo(streamIdentity.Guid);
            if (result != 0) return false;

            int readOffset = 0;
            string decodedStreamNamespace = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            return string.Compare(decodedStreamNamespace, streamIdentity.Namespace, StringComparison.Ordinal) == 0;
        }
    }

    /// <summary>
    /// Default event hub data adapter.  Users may subclass to override event data to stream mapping.
    /// </summary>
    public class EventHubDataAdapter : ICacheDataAdapter<EventData, CachedEventHubMessage>
    {
        private readonly IObjectPool<FixedSizeBuffer> bufferPool;
        private readonly TimePurgePredicate timePurage;
        private FixedSizeBuffer currentBuffer;

        /// <summary>
        /// Assignable purge action.  This is called when a purge request is triggered.
        /// </summary>
        public Action<IDisposable> PurgeAction { private get; set; }

        /// <summary>
        /// Cache data adapter that adapts EventHub's EventData to CachedEventHubMessage used in cache
        /// </summary>
        /// <param name="bufferPool"></param>
        /// <param name="timePurage"></param>
        public EventHubDataAdapter(IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurage = null)
        {
            if (bufferPool == null)
            {
                throw new ArgumentNullException(nameof(bufferPool));
            }
            this.bufferPool = bufferPool;
            this.timePurage = timePurage ?? TimePurgePredicate.Default;
        }

        /// <summary>
        /// Converts a TQueueMessage message from the queue to a TCachedMessage cachable structures.
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="queueMessage"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        public StreamPosition QueueMessageToCachedMessage(ref CachedEventHubMessage cachedMessage, EventData queueMessage, DateTime dequeueTimeUtc)
        {
            StreamPosition streamPosition = GetStreamPosition(queueMessage);
            cachedMessage.StreamGuid = streamPosition.StreamIdentity.Guid;
            cachedMessage.SequenceNumber = queueMessage.SequenceNumber;
            cachedMessage.EnqueueTimeUtc = queueMessage.EnqueuedTimeUtc;
            cachedMessage.DequeueTimeUtc = dequeueTimeUtc;
            cachedMessage.Segment = EncodeMessageIntoSegment(streamPosition, queueMessage);
            return streamPosition;
        }

        /// <summary>
        /// Converts a cached message to a batch container for delivery
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        public IBatchContainer GetBatchContainer(ref CachedEventHubMessage cachedMessage)
        {
            var evenHubMessage = new EventHubMessage(cachedMessage);
            return GetBatchContainer(evenHubMessage);
        }

        /// <summary>
        /// Convert an EventHubMessage to a batch container
        /// </summary>
        /// <param name="eventHubMessage"></param>
        /// <returns></returns>
        protected virtual IBatchContainer GetBatchContainer(EventHubMessage eventHubMessage)
        {
            return new EventHubBatchContainer(eventHubMessage);
        }

        /// <summary>
        /// Gets the stream sequence token from a cached message.
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        public virtual StreamSequenceToken GetSequenceToken(ref CachedEventHubMessage cachedMessage)
        {
            return new EventHubSequenceTokenV2("", cachedMessage.SequenceNumber, 0);
        }

        /// <summary>
        /// Gets the stream position from a queue message
        /// </summary>
        /// <param name="queueMessage"></param>
        /// <returns></returns>
        public virtual StreamPosition GetStreamPosition(EventData queueMessage)
        {
            Guid streamGuid = Guid.Parse(queueMessage.PartitionKey);
            string streamNamespace = queueMessage.GetStreamNamespaceProperty();
            IStreamIdentity stremIdentity = new StreamIdentity(streamGuid, streamNamespace);
            StreamSequenceToken token = new EventHubSequenceTokenV2(queueMessage.Offset, queueMessage.SequenceNumber, 0); 

            return new StreamPosition(stremIdentity, token);
        }

        /// <summary>
        /// Given a purge request, indicates if a cached message should be purged from the cache
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="newestCachedMessage"></param>
        /// <param name="purgeRequest"></param>
        /// <param name="nowUtc"></param>
        /// <returns></returns>
        public bool ShouldPurge(ref CachedEventHubMessage cachedMessage, ref CachedEventHubMessage newestCachedMessage, IDisposable purgeRequest, DateTime nowUtc)
        {
            // if we're purging our current buffer, don't use it any more
            var purgedResource = (FixedSizeBuffer)purgeRequest;
            if (currentBuffer != null && currentBuffer.Id == purgedResource.Id)
            {
                currentBuffer = null;
            }

            TimeSpan timeInCache = nowUtc - cachedMessage.DequeueTimeUtc;
            // age of message relative to the most recent event in the cache.
            TimeSpan relativeAge = newestCachedMessage.EnqueueTimeUtc - cachedMessage.EnqueueTimeUtc;

            return ShouldPurgeFromResource(ref cachedMessage, purgedResource) || timePurage.ShouldPurgFromTime(timeInCache, relativeAge);
        }

        private static bool ShouldPurgeFromResource(ref CachedEventHubMessage cachedMessage, FixedSizeBuffer purgedResource)
        {
            // if message is from this resource, purge
            return cachedMessage.Segment.Array == purgedResource.Id;
        }

        private ArraySegment<byte> GetSegment(int size)
        {
            // get segment from current block
            ArraySegment<byte> segment;
            if (currentBuffer == null || !currentBuffer.TryGetSegment(size, out segment))
            {
                // no block or block full, get new block and try again
                currentBuffer = bufferPool.Allocate();
                currentBuffer.SetPurgeAction(PurgeAction);
                // if this fails with clean block, then requested size is too big
                if (!currentBuffer.TryGetSegment(size, out segment))
                {
                    string errmsg = String.Format(CultureInfo.InvariantCulture,
                        "Message size is to big. MessageSize: {0}", size);
                    throw new ArgumentOutOfRangeException(nameof(size), errmsg);
                }
            }
            return segment;
        }

        // Placed object message payload into a segment.
        private ArraySegment<byte> EncodeMessageIntoSegment(StreamPosition streamPosition, EventData queueMessage)
        {
            byte[] propertiesBytes = queueMessage.SerializeProperties();
            byte[] payload = queueMessage.GetBytes();
            // get size of namespace, offset, partitionkey, properties, and payload
            int size = SegmentBuilder.CalculateAppendSize(streamPosition.StreamIdentity.Namespace) +
            SegmentBuilder.CalculateAppendSize(queueMessage.Offset) +
            SegmentBuilder.CalculateAppendSize(queueMessage.PartitionKey) +
            SegmentBuilder.CalculateAppendSize(propertiesBytes) +
            SegmentBuilder.CalculateAppendSize(payload);

            // get segment
            ArraySegment<byte> segment = GetSegment(size);

            // encode namespace, offset, partitionkey, properties and payload into segment
            int writeOffset = 0;
            SegmentBuilder.Append(segment, ref writeOffset, streamPosition.StreamIdentity.Namespace);
            SegmentBuilder.Append(segment, ref writeOffset, queueMessage.Offset);
            SegmentBuilder.Append(segment, ref writeOffset, queueMessage.PartitionKey);
            SegmentBuilder.Append(segment, ref writeOffset, propertiesBytes);
            SegmentBuilder.Append(segment, ref writeOffset, payload);

            return segment;
        }
    }
}
