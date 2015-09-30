/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Globalization;

using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    [Serializable]
    public class EventSequenceToken : StreamSequenceToken
    {
        private readonly long sequenceNumber;
        private readonly int eventIndex;

        public EventSequenceToken(long seqNumber)
        {
            sequenceNumber = seqNumber;
            eventIndex = 0;
        }

        internal EventSequenceToken(long seqNumber, int eventInd)
        {
            sequenceNumber = seqNumber;
            eventIndex = eventInd;
        }

        public EventSequenceToken NextSequenceNumber()
        {
            return new EventSequenceToken(sequenceNumber + 1, eventIndex);
        }

        public long GetSequenceNumber()
        {
            return sequenceNumber;
        }

        public EventSequenceToken CreateSequenceTokenForEvent(int eventInd)
        {
            return new EventSequenceToken(sequenceNumber, eventInd);
        }

        internal static long Distance(EventSequenceToken first, EventSequenceToken second)
        {
            return first.sequenceNumber - second.sequenceNumber;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as EventSequenceToken);
        }

        public override bool Equals(StreamSequenceToken other)
        {
            var token = other as EventSequenceToken;
            return token != null && (token.sequenceNumber == sequenceNumber &&
                                     token.eventIndex == eventIndex);
        }

        public override int CompareTo(StreamSequenceToken other)
        {
            if (other == null)
                return 1;
            
            var token = other as EventSequenceToken;
            if (token == null)
                throw new ArgumentOutOfRangeException("other");
            
            int difference = sequenceNumber.CompareTo(token.sequenceNumber);
            return difference != 0 ? difference : eventIndex.CompareTo(token.eventIndex);
        }

        public override int GetHashCode()
        {
            // why 397?
            return (eventIndex * 397) ^ sequenceNumber.GetHashCode();
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[EventSequenceToken: SeqNum={0}, EventIndex={1}]", sequenceNumber, eventIndex);
        }
    }
}
