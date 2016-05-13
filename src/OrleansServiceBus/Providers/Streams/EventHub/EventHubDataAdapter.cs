
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
        public Guid StreamGuid;
        public long SequenceNumber;
        public DateTime EnqueueTimeUtc;
        public DateTime DequeueTimeUtc;
        public ArraySegment<byte> Segment;
    }

    /// <summary>
    /// Replication of EventHub EventData class, reconstructed from cached data CachedEventHubMessage
    /// </summary>
    [Serializable]
    public class EventHubMessage
    {
        public EventHubMessage(CachedEventHubMessage cachedMessage)
        {
            int readOffset = 0;
            StreamIdentity = new StreamIdentity(cachedMessage.StreamGuid, SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset));
            SequenceNumber = cachedMessage.SequenceNumber;
            EnqueueTimeUtc = cachedMessage.EnqueueTimeUtc;
            DequeueTimeUtc = cachedMessage.DequeueTimeUtc;
            Properties = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset).DeserializeProperties();
            object offsetObj;
            Offset = Properties.TryGetValue("Offset", out offsetObj)
                ? offsetObj as string
                : default(string);
            PartitionKey = Properties.TryGetValue("PartitionKey", out offsetObj)
                ? offsetObj as string
                : default(string);
            Payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset).ToArray();
        }

        public IStreamIdentity StreamIdentity { get; }
        public string PartitionKey { get; }
        public string Offset { get; }
        public long SequenceNumber { get; }
        public DateTime EnqueueTimeUtc { get; }
        public DateTime DequeueTimeUtc { get; }
        public IDictionary<string, object> Properties { get; }
        public byte[] Payload { get; }
    }

    /// <summary>
    /// Default eventhub data comparer.  Implements comparisions against CachedEventHubMessage
    /// </summary>
    internal class EventHubDataComparer : ICacheDataComparer<CachedEventHubMessage>
    {
        public static readonly ICacheDataComparer<CachedEventHubMessage> Instance = new EventHubDataComparer();

        public int Compare(CachedEventHubMessage cachedMessage, StreamSequenceToken token)
        {
            var realToken = (EventSequenceToken)token;
            return cachedMessage.SequenceNumber != realToken.SequenceNumber
                ? (int)(cachedMessage.SequenceNumber - realToken.SequenceNumber)
                : 0 - realToken.EventIndex;
        }

        public int Compare(CachedEventHubMessage cachedMessage, IStreamIdentity streamIdentity)
        {
            int result = cachedMessage.StreamGuid.CompareTo(streamIdentity.Guid);
            if (result != 0) return result;

            int readOffset = 0;
            string decodedStreamNamespace = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            return string.Compare(decodedStreamNamespace, streamIdentity.Namespace, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Default event hub data adapter.  Users may subclass to override event data to stream mapping.
    /// </summary>
    public class EventHubDataAdapter : ICacheDataAdapter<EventData, CachedEventHubMessage>
    {
        private readonly IObjectPool<FixedSizeBuffer> bufferPool;
        private FixedSizeBuffer currentBuffer;

        public Action<IDisposable> PurgeAction { private get; set; }

        public EventHubDataAdapter(IObjectPool<FixedSizeBuffer> bufferPool)
        {
            if (bufferPool == null)
            {
                throw new ArgumentNullException("bufferPool");
            }
            this.bufferPool = bufferPool;
        }

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

        public IBatchContainer GetBatchContainer(ref CachedEventHubMessage cachedMessage)
        {
            var evenHubMessage = new EventHubMessage(cachedMessage);
            return GetBatchContainer(evenHubMessage);
        }

        protected virtual IBatchContainer GetBatchContainer(EventHubMessage eventHubMessage)
        {
            return new EventHubBatchContainer(eventHubMessage);
        }

        public virtual StreamSequenceToken GetSequenceToken(ref CachedEventHubMessage cachedMessage)
        {
            return new EventSequenceToken(cachedMessage.SequenceNumber, 0);
        }

        public virtual StreamPosition GetStreamPosition(EventData queueMessage)
        {
            Guid streamGuid = Guid.Parse(queueMessage.PartitionKey);
            string streamNamespace = queueMessage.GetStreamNamespaceProperty();
            IStreamIdentity stremIdentity = new StreamIdentity(streamGuid, streamNamespace);
            StreamSequenceToken token = new EventSequenceToken(queueMessage.SequenceNumber, 0);
            return new StreamPosition(stremIdentity, token);
        }

        public bool ShouldPurge(ref CachedEventHubMessage cachedMessage, IDisposable purgeRequest)
        {
            var purgedResource = (FixedSizeBuffer) purgeRequest;
            // if we're purging our current buffer, don't use it any more
            if (currentBuffer != null && currentBuffer.Id == purgedResource.Id)
            {
                currentBuffer = null;
            }
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
                    throw new ArgumentOutOfRangeException("size", errmsg);
                }
            }
            return segment;
        }

        // Placed object message payload into a segment.
        private ArraySegment<byte> EncodeMessageIntoSegment(StreamPosition streamPosition, EventData queueMessage)
        {
            byte[] propertiesBytes = queueMessage.Properties.SerializeProperties();
            byte[] payload = queueMessage.GetBytes();
            // get size of namespace, offset, properties, and payload
            int size = SegmentBuilder.CalculateAppendSize(streamPosition.StreamIdentity.Namespace) +
            SegmentBuilder.CalculateAppendSize(propertiesBytes) +
            SegmentBuilder.CalculateAppendSize(payload);

            // get segment
            ArraySegment<byte> segment = GetSegment(size);

            // encode namespace, offset, properties and payload into segment
            int writeOffset = 0;
            SegmentBuilder.Append(segment, ref writeOffset, streamPosition.StreamIdentity.Namespace);
            SegmentBuilder.Append(segment, ref writeOffset, propertiesBytes);
            SegmentBuilder.Append(segment, ref writeOffset, payload);

            return segment;
        }
    }
}
