
using System;
using System.Globalization;
using System.Linq;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.ServiceBus.Providers.Streams.EventHub;
using Orleans.Streams;

namespace OrleansServiceBusUtils.Providers.Streams.EventHub
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

    class EventHubDataAdapter : ICacheDataAdapter<EventData, CachedEventHubMessage>
    {
        private readonly IObjectPool<FixedSizeBuffer> bufferPool;
        private readonly Action<IDisposable> purgeAction;
        private FixedSizeBuffer currentBuffer;

        public EventHubDataAdapter(IObjectPool<FixedSizeBuffer> bufferPool, Action<IDisposable> purgeAction)
        {
            if (bufferPool == null)
            {
                throw new ArgumentNullException("bufferPool");
            }
            if (purgeAction == null)
            {
                throw new ArgumentNullException("purgeAction");
            }
            this.bufferPool = bufferPool;
            this.purgeAction = purgeAction;
        }

        public void QueueMessageToCachedMessage(ref CachedEventHubMessage cachedMessage, EventData queueMessage)
        {
            cachedMessage.StreamGuid = Guid.Parse(queueMessage.PartitionKey);
            cachedMessage.SequenceNumber = queueMessage.SequenceNumber;
            cachedMessage.Segment = SerializeMessageIntoPooledSegment(queueMessage);
        }

        // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
        private ArraySegment<byte> SerializeMessageIntoPooledSegment(EventData queueMessage)
        {
            byte[] payloadBytes = queueMessage.GetBytes();
            string streamNamespace = queueMessage.GetStreamNamespaceProperty();
            int size = CalculateAppendSize(streamNamespace) + CalculateAppendSize(queueMessage.Offset) + CalculateAppendSize(payloadBytes);

            // get segment from current block
            ArraySegment<byte> segment;
            if (currentBuffer == null || !currentBuffer.TryGetSegment(size, out segment))
            {
                // no block or block full, get new block and try again
                currentBuffer = bufferPool.Allocate();
                currentBuffer.SetPurgeAction(purgeAction);
                // if this fails with clean block, then requested size is too big
                if (!currentBuffer.TryGetSegment(size, out segment))
                {
                    string errmsg = String.Format(CultureInfo.InvariantCulture,
                        "Message size is to big. MessageSize: {0}", size);
                    throw new ArgumentOutOfRangeException("queueMessage", errmsg);
                }
            }
            // encode namespace, offset, and payload into segment
            int writeOffset = 0;
            Append(segment, ref writeOffset, streamNamespace);
            Append(segment, ref writeOffset, queueMessage.Offset);
            Append(segment, ref writeOffset, payloadBytes);

            return segment;
        }

        public IBatchContainer GetBatchContainer(ref CachedEventHubMessage cachedMessage)
        {
            int readOffset = 0;
            string streamNamespace = ReadNextString(cachedMessage.Segment, ref readOffset);
            string offset = ReadNextString(cachedMessage.Segment, ref readOffset);
            ArraySegment<byte> payload = ReadNextBytes(cachedMessage.Segment, ref readOffset);

            return new EventHubBatchContainer(cachedMessage.StreamGuid, streamNamespace, offset, cachedMessage.SequenceNumber, payload.ToArray());
        }

        public StreamSequenceToken GetSequenceToken(ref CachedEventHubMessage cachedMessage)
        {
            return new EventSequenceToken(cachedMessage.SequenceNumber, 0);
        }

        public int CompareCachedMessageToSequenceToken(ref CachedEventHubMessage cachedMessage, StreamSequenceToken token)
        {
            var realToken = (EventSequenceToken) token;
            return cachedMessage.SequenceNumber != realToken.SequenceNumber
                ? (int) (cachedMessage.SequenceNumber - realToken.SequenceNumber)
                : 0 - realToken.EventIndex;
        }

        public bool IsInStream(ref CachedEventHubMessage cachedMessage, Guid streamGuid, string streamNamespace)
        {
            // fail out early if guids does not match.  Don't incur cost of decoding namespace unless necessary.
            if (cachedMessage.StreamGuid != streamGuid)
            {
                return false;
            }
            int readOffset = 0;
            string decodedStreamNamespace = ReadNextString(cachedMessage.Segment, ref readOffset);
            return decodedStreamNamespace == streamNamespace;
        }

        public bool ShouldPurge(CachedEventHubMessage cachedMessage, IDisposable purgeRequest)
        {
            var purgedResource = (FixedSizeBuffer) purgeRequest;
            // if we're purging our current buffer, don't use it any more
            if (currentBuffer != null && currentBuffer.Id == purgedResource.Id)
            {
                currentBuffer = null;
            }
            return cachedMessage.Segment.Array == purgedResource.Id;
        }

        /// <summary>
        /// Calculates how much space will be needed to append the provided bytes into the segment.
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        private static int CalculateAppendSize(byte[] bytes)
        {
            return (bytes == null || bytes.Length == 0)
                ? sizeof(int)
                : bytes.Length + sizeof(int);
        }

        /// <summary>
        /// Calculates how much space will be needed to append the provided string into the segment.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        private static int CalculateAppendSize(string str)
        {
            return (string.IsNullOrEmpty(str))
                ? sizeof(int)
                : str.Length * sizeof(char) + sizeof(int);
        }

        /// <summary>
        /// Appends an array of bytes to the end of the segment
        /// </summary>
        /// <param name="writerOffset"></param>
        /// <param name="bytes"></param>
        /// <param name="segment"></param>
        private static void Append(ArraySegment<byte> segment, ref int writerOffset, byte[] bytes)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException("segment");
            }
            if (bytes == null)
            {
                throw new ArgumentNullException("bytes");
            }

            if (bytes.Length == 0)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, segment.Array, segment.Offset + writerOffset, sizeof(int));
                writerOffset += sizeof(int);
            }
            else
            {
                Buffer.BlockCopy(BitConverter.GetBytes(bytes.Length), 0, segment.Array, segment.Offset + writerOffset, sizeof(int));
                writerOffset += sizeof(int);
                Buffer.BlockCopy(bytes, 0, segment.Array, segment.Offset + writerOffset, bytes.Length);
                writerOffset += bytes.Length;
            }
        }

        /// <summary>
        /// Appends a string to the end of the segment
        /// </summary>
        /// <param name="writerOffset"></param>
        /// <param name="str"></param>
        /// <param name="segment"></param>
        private static void Append(ArraySegment<byte> segment, ref int writerOffset, string str)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException("segment");
            }
            if (str == null)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(-1), 0, segment.Array, segment.Offset + writerOffset, sizeof(int));
                writerOffset += sizeof(int);
            }
            else if (string.IsNullOrEmpty(str))
            {
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, segment.Array, segment.Offset + writerOffset, sizeof(int));
                writerOffset += sizeof(int);
            }
            else
            {
                var bytes = new byte[str.Length * sizeof(char)];
                Buffer.BlockCopy(str.ToCharArray(), 0, bytes, 0, bytes.Length);
                Append(segment, ref writerOffset, bytes);
            }
        }

        /// <summary>
        /// Reads the next item in the segment as a byte array.  For performance, this is returned as a sub-segment of the original segment.
        /// </summary>
        /// <returns></returns>
        private static ArraySegment<byte> ReadNextBytes(ArraySegment<byte> segment, ref int readerOffset)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException("segment");
            }
            int size = BitConverter.ToInt32(segment.Array, segment.Offset + readerOffset);
            readerOffset += sizeof(int);
            var seg = new ArraySegment<byte>(segment.Array, segment.Offset + readerOffset, size);
            readerOffset += size;
            return seg;
        }

        /// <summary>
        /// Reads the next item in the segment as a string.
        /// </summary>
        /// <returns></returns>
        private static string ReadNextString(ArraySegment<byte> segment, ref int readerOffset)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException("segment");
            }
            int size = BitConverter.ToInt32(segment.Array, segment.Offset + readerOffset);
            readerOffset += sizeof(int);
            if (size < 0)
            {
                return null;
            }
            if (size == 0)
            {
                return string.Empty;
            }
            var chars = new char[size / sizeof(char)];
            Buffer.BlockCopy(segment.Array, segment.Offset + readerOffset, chars, 0, size);
            readerOffset += size;
            return new string(chars);
        }
    }
}
