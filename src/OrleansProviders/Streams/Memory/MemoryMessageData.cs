
using System;

namespace Orleans.Providers
{
    /// <summary>
    /// Represents the event sent and received from an In-Memory queue grain. 
    /// </summary>
    [Serializable]
    public struct MemoryMessageData
    {
        /// <summary>
        /// Stream Guid of the event data.
        /// </summary>
        public Guid StreamGuid;
        /// <summary>
        /// Stream namespace.
        /// </summary>
        public string StreamNamespace;
        /// <summary>
        /// Position of even in stream.
        /// </summary>
        public long SequenceNumber;

        /// <summary>
        /// Time message was read from message queue
        /// </summary>
        public DateTime DequeueTimeUtc;

        /// <summary>
        /// Time message was written to message queue
        /// </summary>
        public DateTime EnqueueTimeUtc;

        /// <summary>
        /// Serialized event data.
        /// </summary>
        public ArraySegment<byte> Payload;

        internal static MemoryMessageData Create(Guid streamGuid, String streamNamespace, ArraySegment<byte> arraySegment)
        {
            return new MemoryMessageData
            {
                StreamGuid = streamGuid,
                StreamNamespace = streamNamespace,
                EnqueueTimeUtc = DateTime.UtcNow,
                Payload = arraySegment
            };
        }
    }
}
