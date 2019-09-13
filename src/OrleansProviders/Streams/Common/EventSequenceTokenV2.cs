using System;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Stream sequence token that tracks sequence number and event index
    /// </summary>
    [Serializable]
    public class EventSequenceTokenV2 : StreamSequenceToken
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
            => this.sequenceToken ?? (this.sequenceToken = EventSequenceToken.GenerateToken(this.SequenceNumber, this.EventIndex));

        /// <summary>
        /// Sequence token constructor
        /// </summary>
        /// <param name="seqNumber"></param>
        public EventSequenceTokenV2(long seqNumber)
        {
            this.SequenceNumber = seqNumber;
            this.EventIndex = 0;
        }

        public EventSequenceTokenV2(byte[] token)
        {
            EventSequenceToken.Parse(token, out long seqNumber, out int index);
            this.SequenceNumber = seqNumber;
            this.EventIndex = index;
            this.sequenceToken = token;
        }

        /// <summary>
        /// Sequence token constructor
        /// </summary>
        /// <param name="seqNumber"></param>
        /// <param name="eventInd"></param>
        public EventSequenceTokenV2(long seqNumber, int eventInd)
        {
            this.SequenceNumber = seqNumber;
            this.EventIndex = eventInd;
        }

        /// <summary>
        /// Creates a sequence token for a specific event in the current batch
        /// </summary>
        /// <param name="eventInd"></param>
        /// <returns></returns>
        public EventSequenceTokenV2 CreateSequenceTokenForEvent(int eventInd)
            => new EventSequenceTokenV2(this.SequenceNumber, eventInd);

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
    }
}
