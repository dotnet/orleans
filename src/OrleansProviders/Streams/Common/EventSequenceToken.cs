using System;
using System.Globalization;

using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    [Serializable]
    public class EventSequenceToken : StreamSequenceToken
    {
        public long SequenceNumber { get; set; }
        
        public int EventIndex { get; set; }

        public EventSequenceToken(long seqNumber)
        {
            SequenceNumber = seqNumber;
            EventIndex = 0;
        }

        public EventSequenceToken(long seqNumber, int eventInd)
        {
            SequenceNumber = seqNumber;
            EventIndex = eventInd;
        }

        public EventSequenceToken NextSequenceNumber()
        {
            return new EventSequenceToken(SequenceNumber + 1, EventIndex);
        }

        public EventSequenceToken CreateSequenceTokenForEvent(int eventInd)
        {
            return new EventSequenceToken(SequenceNumber, eventInd);
        }

        internal static long Distance(EventSequenceToken first, EventSequenceToken second)
        {
            return first.SequenceNumber - second.SequenceNumber;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as EventSequenceToken);
        }

        public override bool Equals(StreamSequenceToken other)
        {
            var token = other as EventSequenceToken;
            return token != null && (token.SequenceNumber == SequenceNumber &&
                                     token.EventIndex == EventIndex);
        }

        public override int CompareTo(StreamSequenceToken other)
        {
            if (other == null)
                return 1;
            
            var token = other as EventSequenceToken;
            if (token == null)
                throw new ArgumentOutOfRangeException("other");
            
            int difference = SequenceNumber.CompareTo(token.SequenceNumber);
            return difference != 0 ? difference : EventIndex.CompareTo(token.EventIndex);
        }

        public override int GetHashCode()
        {
            // why 397?
            return (EventIndex * 397) ^ SequenceNumber.GetHashCode();
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[EventSequenceToken: SeqNum={0}, EventIndex={1}]", SequenceNumber, EventIndex);
        }
    }
}
