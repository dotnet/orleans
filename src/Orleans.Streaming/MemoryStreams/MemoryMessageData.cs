
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
        /// Stream Guid of the event data.
        /// </summary>
        [Id(0)]
        public StreamId StreamId;

        /// <summary>
        /// Position of even in stream.
        /// </summary>
        [Id(1)]
        public long SequenceNumber;

        /// <summary>
        /// Time message was read from message queue
        /// </summary>
        [Id(2)]
        public DateTime DequeueTimeUtc;

        /// <summary>
        /// Time message was written to message queue
        /// </summary>
        [Id(3)]
        public DateTime EnqueueTimeUtc;

        /// <summary>
        /// Serialized event data.
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
