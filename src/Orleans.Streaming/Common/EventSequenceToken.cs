using System;
using System.Globalization;
using Orleans.Streams;
using Newtonsoft.Json;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Stream sequence token that tracks sequence number and event index
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class EventSequenceToken : StreamSequenceToken
    {
        /// <summary>
        /// Gets the number of event batches in stream prior to this event batch
        /// </summary>
        [Id(0)]
        [JsonProperty]
        public override long SequenceNumber { get; protected set; }

        /// <summary>
        /// Gets the number of events in batch prior to this event
        /// </summary>
        [Id(1)]
        [JsonProperty]
        public override int EventIndex { get; protected set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventSequenceToken"/> class.
        /// </summary>
        /// <param name="sequenceNumber">The sequence number.</param>
        public EventSequenceToken(long sequenceNumber)
        {
            SequenceNumber = sequenceNumber;
            EventIndex = 0;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventSequenceToken" /> class.
        /// </summary>
        /// <param name="sequenceNumber">The sequence number.</param>
        /// <param name="eventIndex">The event index, for events which are part of a batch.</param>
        public EventSequenceToken(long sequenceNumber, int eventIndex)
        {
            SequenceNumber = sequenceNumber;
            EventIndex = eventIndex;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventSequenceToken" /> class.
        /// </summary>
        /// <remarks>
        /// This constructor is exposed for serializer use only.
        /// </remarks>
        [JsonConstructor]
        public EventSequenceToken()
        { }

        /// <summary>
        /// Creates a sequence token for a specific event in the current batch.
        /// </summary>
        /// <param name="eventInd">The event index, for events which are part of a batch.</param>
        /// <returns>The sequence token.</returns>
        public EventSequenceToken CreateSequenceTokenForEvent(int eventInd)
        {
            return new EventSequenceToken(SequenceNumber, eventInd);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return Equals(obj as EventSequenceToken);
        }

        /// <inheritdoc />
        public override bool Equals(StreamSequenceToken other)
        {
            var token = other as EventSequenceToken;
            return token != null && (token.SequenceNumber == SequenceNumber &&
                                     token.EventIndex == EventIndex);
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return (EventIndex * 397) ^ SequenceNumber.GetHashCode();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[EventSequenceToken: SeqNum={0}, EventIndex={1}]", SequenceNumber, EventIndex);
        }
    }
}
