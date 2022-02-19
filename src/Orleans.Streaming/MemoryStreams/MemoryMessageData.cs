
using System;
using Orleans.Runtime;

namespace Orleans.Providers
{
    /// <summary>
    /// Represents the event sent and received from an In-Memory queue grain. 
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public struct MemoryMessageData
    {
        /// <summary>
        /// The stream identifier.
        /// </summary>
        [Id(0)]
        public StreamId StreamId;

        /// <summary>
        /// The position of the event in the stream.
        /// </summary>
        [Id(1)]
        public long SequenceNumber;

        /// <summary>
        /// The time this message was read from the message queue.
        /// </summary>
        [Id(2)]
        public DateTime DequeueTimeUtc;

        /// <summary>
        /// The time message was written to the message queue.
        /// </summary>
        [Id(3)]
        public DateTime EnqueueTimeUtc;

        /// <summary>
        /// The serialized event data.
        /// </summary>
        [Id(4)]
        public ArraySegment<byte> Payload;

        internal static MemoryMessageData Create(StreamId streamId, ArraySegment<byte> arraySegment)
        {
            return new MemoryMessageData
            {
                StreamId = streamId,
                EnqueueTimeUtc = DateTime.UtcNow,
                Payload = arraySegment
            };
        }
    }
}
