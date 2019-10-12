
using System;
using Orleans.Providers.Abstractions;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// This is a tightly packed cached structure containing a queue message.
    /// Other than a pooled byte array, it should only contain value types.
    /// </summary>
    public struct CachedMessage
    {
        /// <summary>
        /// location of sequence token in segment
        /// </summary>
        public (int Offset, int Count) SequenceTokenWindow;
        /// <summary>
        /// location of streamId in segment
        /// </summary>
        public (int Offset, int Count) StreamIdTokenWindow;
        /// <summary>
        /// location of payload in segment
        /// </summary>
        public (int Offset, int Count) OffsetTokenWindow;
        /// <summary>
        /// location of payload in segment
        /// </summary>
        public (int Offset, int Count) PayloadWindow;
        /// <summary>
        /// Time event was written to EventHub
        /// </summary>
        public DateTime EnqueueTimeUtc;
        /// <summary>
        /// Time event was read from EventHub into this cache
        /// </summary>
        public DateTime DequeueTimeUtc;
        /// <summary>
        /// Segment containing the serialized event data
        /// </summary>
        private ArraySegment<byte> Segment;
        public object Id => Segment.Array;

        public static CachedMessage Create(IQueueMessageCacheAdapter adapter, in DateTime dequeueTime, Func<int, ArraySegment<byte>> getSegment)
        {
            byte[] streamIdentityToken = StreamIdentityToken.Create(adapter.StreamPosition.StreamIdentity);
            var cachedMessage = new CachedMessage
            {
                DequeueTimeUtc = dequeueTime,
                EnqueueTimeUtc = adapter.EnqueueTimeUtc
            };

            cachedMessage.SequenceTokenWindow = (0, adapter.StreamPosition.SequenceToken.SequenceToken.Length);
            cachedMessage.StreamIdTokenWindow = (cachedMessage.SequenceTokenWindow.Offset + cachedMessage.SequenceTokenWindow.Count, streamIdentityToken.Length);
            cachedMessage.OffsetTokenWindow = (cachedMessage.StreamIdTokenWindow.Offset + cachedMessage.StreamIdTokenWindow.Count, adapter.OffsetToken.Length);
            cachedMessage.PayloadWindow = (cachedMessage.OffsetTokenWindow.Offset + cachedMessage.OffsetTokenWindow.Count, adapter.PayloadSize);

            // get size of namespace, offset, partitionkey, properties, and payload
            int size =
                cachedMessage.SequenceTokenWindow.Count +
                cachedMessage.StreamIdTokenWindow.Count +
                cachedMessage.OffsetTokenWindow.Count +
                cachedMessage.PayloadWindow.Count;

            // get segment
            cachedMessage.Segment = getSegment(size);

            // encode sequence token
            Buffer.BlockCopy(adapter.StreamPosition.SequenceToken.SequenceToken, 0, cachedMessage.Segment.Array, cachedMessage.Segment.Offset + cachedMessage.SequenceTokenWindow.Offset, cachedMessage.SequenceTokenWindow.Count);

            // encode streamIdentityToken
            Buffer.BlockCopy(streamIdentityToken, 0, cachedMessage.Segment.Array, cachedMessage.Segment.Offset + cachedMessage.StreamIdTokenWindow.Offset, cachedMessage.StreamIdTokenWindow.Count);

            // encode offsetToken
            Buffer.BlockCopy(adapter.OffsetToken, 0, cachedMessage.Segment.Array, cachedMessage.Segment.Offset + cachedMessage.OffsetTokenWindow.Offset, cachedMessage.OffsetTokenWindow.Count);

            // encode payload
            adapter.AppendPayload(cachedMessage.Payload());

            return cachedMessage;
        }

        public static CachedMessage Create(byte[] sequenceToken, byte[] streamIdentityToken, byte[] offsetToken, byte[] body, in DateTime enqueueTimeUtc, in DateTime dequeueTime, Func<int, ArraySegment<byte>> getSegment)
        {
            var cachedMessage = new CachedMessage
            {
                DequeueTimeUtc = dequeueTime,
                EnqueueTimeUtc = enqueueTimeUtc
            };

            cachedMessage.SequenceTokenWindow = (0, sequenceToken.Length);
            cachedMessage.StreamIdTokenWindow = (cachedMessage.SequenceTokenWindow.Offset + cachedMessage.SequenceTokenWindow.Count, streamIdentityToken.Length);
            cachedMessage.OffsetTokenWindow = (cachedMessage.StreamIdTokenWindow.Offset + cachedMessage.StreamIdTokenWindow.Count, offsetToken.Length);
            cachedMessage.PayloadWindow = (cachedMessage.OffsetTokenWindow.Offset + cachedMessage.OffsetTokenWindow.Count, body.Length);

            // get size of namespace, offset, partitionkey, properties, and payload
            int size =
                cachedMessage.SequenceTokenWindow.Count +
                cachedMessage.StreamIdTokenWindow.Count +
                cachedMessage.OffsetTokenWindow.Count +
                cachedMessage.PayloadWindow.Count;

            // get segment
            cachedMessage.Segment = getSegment(size);

            // encode sequence token
            Buffer.BlockCopy(sequenceToken, 0, cachedMessage.Segment.Array, cachedMessage.Segment.Offset + cachedMessage.SequenceTokenWindow.Offset, cachedMessage.SequenceTokenWindow.Count);

            // encode streamIdentityToken
            Buffer.BlockCopy(streamIdentityToken, 0, cachedMessage.Segment.Array, cachedMessage.Segment.Offset + cachedMessage.StreamIdTokenWindow.Offset, cachedMessage.StreamIdTokenWindow.Count);

            // encode offsetToken
            Buffer.BlockCopy(offsetToken, 0, cachedMessage.Segment.Array, cachedMessage.Segment.Offset + cachedMessage.OffsetTokenWindow.Offset, cachedMessage.OffsetTokenWindow.Count);

            // encode payload
            Buffer.BlockCopy(body, 0, cachedMessage.Segment.Array, cachedMessage.Segment.Offset + cachedMessage.PayloadWindow.Offset, cachedMessage.PayloadWindow.Count);

            return cachedMessage;
        }

        public ArraySegment<byte> SequenceToken()
            => this.Segment.Spit(this.SequenceTokenWindow.Offset, this.SequenceTokenWindow.Count);

        public ArraySegment<byte> StreamIdToken()
            => this.Segment.Spit(this.StreamIdTokenWindow.Offset, this.StreamIdTokenWindow.Count);

        public ArraySegment<byte> OffsetToken()
            => this.Segment.Spit(this.OffsetTokenWindow.Offset, this.OffsetTokenWindow.Count);

        public ArraySegment<byte> Payload()
            => this.Segment.Spit(this.PayloadWindow.Offset, this.PayloadWindow.Count);

        public int Compare(ReadOnlySpan<byte> sequenceToken)
            => this.SequenceToken().AsSpan().SequenceCompareTo(sequenceToken);

        public bool CompareStreamId(byte[] streamIdentity)
            => this.StreamIdToken().AsSpan().SequenceEqual(streamIdentity);
    }

    public static class CachedMessageExtensions
    {
        internal static ArraySegment<byte> Spit(this in ArraySegment<byte> source, int offset, int count)
        {
            if (source.Offset + offset + count > source.Offset + source.Count)
                throw new ArgumentOutOfRangeException(nameof(source));
            return new ArraySegment<byte>(source.Array, source.Offset + offset, count);
        }
    }
}
