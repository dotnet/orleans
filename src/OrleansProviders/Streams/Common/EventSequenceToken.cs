using System;
using System.Linq;
using System.Net;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Stream sequence token that tracks sequence number and event index
    /// </summary>
    [Serializable]
    public class EventSequenceToken : StreamSequenceToken
    {
        /// <summary>
        /// Number of event batches in stream prior to this event batch
        /// </summary>
        public long SequenceNumber { get; protected set; }

        /// <summary>
        /// Number of events in batch prior to this event
        /// </summary>
        public int EventIndex { get; protected set; }

        [NonSerialized] // not serialized to prevent breaking backwards compatability with earlier version
        private byte[] sequenceToken;
        public override byte[] SequenceToken
            => this.sequenceToken ?? (this.sequenceToken = GenerateToken(this.SequenceNumber, this.EventIndex));

        /// <summary>
        /// Sequence token constructor
        /// </summary>
        /// <param name="seqNumber"></param>
        public EventSequenceToken(long seqNumber)
        {
            this.SequenceNumber = seqNumber;
            this.EventIndex = 0;
        }

        /// <summary>
        /// Sequence token constructor
        /// </summary>
        /// <param name="seqNumber"></param>
        /// <param name="eventInd"></param>
        public EventSequenceToken(long seqNumber, int eventInd)
        {
            this.SequenceNumber = seqNumber;
            this.EventIndex = eventInd;
        }

        /// <summary>
        /// Creates a sequence token for a specific event in the current batch
        /// </summary>
        /// <param name="eventInd"></param>
        /// <returns></returns>
        public EventSequenceToken CreateSequenceTokenForEvent(int eventInd)
            => new EventSequenceToken(this.SequenceNumber, eventInd);

        /// <summary>
        /// GetHashCode method for current EventSequenceToken
        /// </summary>
        /// <returns> Hash code for current EventSequenceToken object </returns>
        public override int GetHashCode()
            => (this.EventIndex * 397) ^ this.SequenceNumber.GetHashCode();

        /// <summary>
        /// ToString method
        /// </summary>
        /// <returns> A string which represent current EventSequenceToken object </returns>
        public override string ToString()
            => $"[EventSequenceToken: SeqNum={this.SequenceNumber}, EventIndex={this.EventIndex}]";

        public static byte[] GenerateToken(long sequenceNumber, int eventIndex)
        {
            byte[] sequenceNumberBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(sequenceNumber));
            byte[] eventIndexBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(eventIndex));
            byte[] tokenBytes = new byte[sequenceNumberBytes.Length + eventIndexBytes.Length];
            sequenceNumberBytes.CopyTo(tokenBytes, 0);
            eventIndexBytes.CopyTo(tokenBytes, sequenceNumberBytes.Length);
            return tokenBytes;
        }

        public static void Parse(byte[] token, out long sequenceNumber, out int eventIndex)
        {
            sequenceNumber = IPAddress.NetworkToHostOrder(BitConverter.ToInt64(token, 0));
            eventIndex = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(token, sizeof(long)));
        }
    }
}
