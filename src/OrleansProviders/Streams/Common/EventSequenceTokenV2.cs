using System;
using System.Globalization;
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
        public long SequenceNumber { get;  }

        /// <summary>
        /// Number of events in batch prior to this event
        /// </summary>
        public int EventIndex { get;  }

        /// <summary>
        /// Sequence token constructor
        /// </summary>
        /// <param name="seqNumber"></param>
        public EventSequenceTokenV2(long seqNumber)
        {
            SequenceNumber = seqNumber;
            EventIndex = 0;
        }

        /// <summary>
        /// Sequence token constructor
        /// </summary>
        /// <param name="seqNumber"></param>
        /// <param name="eventInd"></param>
        public EventSequenceTokenV2(long seqNumber, int eventInd)
        {
            SequenceNumber = seqNumber;
            EventIndex = eventInd;
        }

        /// <summary>
        /// Creates a sequence token for a specific event in the current batch
        /// </summary>
        /// <param name="eventInd"></param>
        /// <returns></returns>
        public EventSequenceTokenV2 CreateSequenceTokenForEvent(int eventInd)
        {
            return new EventSequenceTokenV2(SequenceNumber, eventInd);
        }

        /// <summary>
        /// Determines whether the specified object is equal to the current object.
        /// </summary>
        /// <returns>
        /// true if the specified object  is equal to the current object; otherwise, false.
        /// </returns>
        /// <param name="obj">The object to compare with the current object. </param><filterpriority>2</filterpriority>
        public override bool Equals(object obj)
        {
            return Equals(obj as EventSequenceTokenV2);
        }

        /// <summary>
        /// Indicates whether the current object is equal to another object of the same type.
        /// </summary>
        /// <returns>
        /// true if the current object is equal to the <paramref name="other"/> parameter; otherwise, false.
        /// </returns>
        /// <param name="other">An object to compare with this object.</param>
        public override bool Equals(StreamSequenceToken other)
        {
            var token = other as EventSequenceTokenV2;
            return token != null && (token.SequenceNumber == SequenceNumber &&
                                     token.EventIndex == EventIndex);
        }

        /// <summary>
        /// Compares the current object with another object of the same type.
        /// </summary>
        /// <returns>
        /// A value that indicates the relative order of the objects being compared. The return value has the following meanings: Value Meaning Less than zero This object is less than the <paramref name="other"/> parameter.Zero This object is equal to <paramref name="other"/>. Greater than zero This object is greater than <paramref name="other"/>. 
        /// </returns>
        /// <param name="other">An object to compare with this object.</param>
        public override int CompareTo(StreamSequenceToken other)
        {
            if (other == null)
                return 1;

            var token = other as EventSequenceTokenV2;
            if (token == null)
                throw new ArgumentOutOfRangeException(nameof(other));

            int difference = SequenceNumber.CompareTo(token.SequenceNumber);
            return difference != 0 ? difference : EventIndex.CompareTo(token.EventIndex);
        }

        /// <summary>
        /// GetHashCode method for current EventSequenceToken
        /// </summary>
        /// <returns> Hash code for current EventSequenceToken object </returns>
        public override int GetHashCode()
        {
            // why 397?
            return (EventIndex * 397) ^ SequenceNumber.GetHashCode();
        }

        /// <summary>
        /// ToString method
        /// </summary>
        /// <returns> A string which represent current EventSequenceToken object </returns>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[EventSequenceTokenV2: SeqNum={0}, EventIndex={1}]", SequenceNumber, EventIndex);
        }
    }
}
