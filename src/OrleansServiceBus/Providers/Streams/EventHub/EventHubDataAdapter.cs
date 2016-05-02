
using System;
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
        public ArraySegment<byte> Segment;
    }

    /// <summary>
    /// Default eventhub data comparer.  Implements comparisions against CachedEventHubMessage
    /// </summary>
    public class EventHubDataComparer : ICacheDataComparer<CachedEventHubMessage>
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
            return String.Compare(decodedStreamNamespace, streamIdentity.Namespace, StringComparison.Ordinal);
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

        public StreamPosition QueueMessageToCachedMessage(ref CachedEventHubMessage cachedMessage, EventData queueMessage)
        {
            StreamPosition streamPosition = GetStreamPosition(queueMessage);
            cachedMessage.StreamGuid = streamPosition.StreamIdentity.Guid;
            cachedMessage.SequenceNumber = queueMessage.SequenceNumber;
            cachedMessage.Segment = SerializeMessageIntoPooledSegment(streamPosition, queueMessage.Offset, queueMessage.GetBytes());
            return streamPosition;
        }

        // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
        protected ArraySegment<byte> SerializeMessageIntoPooledSegment(StreamPosition streamPosition, string offset, byte[] payloadBytes)
        {
            string streamNamespace = streamPosition.StreamIdentity.Namespace;
            int size = SegmentBuilder.CalculateAppendSize(streamNamespace) +
                       SegmentBuilder.CalculateAppendSize(offset) +
                       SegmentBuilder.CalculateAppendSize(payloadBytes);

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
                    throw new ArgumentOutOfRangeException("payloadBytes", errmsg);
                }
            }
            // encode namespace, offset, and payload into segment
            int writeOffset = 0;
            SegmentBuilder.Append(segment, ref writeOffset, streamNamespace);
            SegmentBuilder.Append(segment, ref writeOffset, offset);
            SegmentBuilder.Append(segment, ref writeOffset, payloadBytes);

            return segment;
        }

        public IBatchContainer GetBatchContainer(ref CachedEventHubMessage cachedMessage)
        {
            int readOffset = 0;
            string streamNamespace = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            string offset = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset);
            ArraySegment<byte> payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset);

            return new EventHubBatchContainer(cachedMessage.StreamGuid, streamNamespace, offset, cachedMessage.SequenceNumber, payload.ToArray());
        }

        public StreamSequenceToken GetSequenceToken(ref CachedEventHubMessage cachedMessage)
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
    }
}
