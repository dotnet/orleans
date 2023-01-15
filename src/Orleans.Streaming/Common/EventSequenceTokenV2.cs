using System;
using System.Globalization;
using Newtonsoft.Json;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Stream sequence token that tracks sequence number and event index
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class EventSequenceTokenV2 : StreamSequenceToken
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
        /// Initializes a new instance of the <see cref="EventSequenceTokenV2"/> class.
        /// </summary>
        /// <param name="seqNumber">The sequence number.</param>
        public EventSequenceTokenV2(long seqNumber)
        {
            SequenceNumber = seqNumber;
            EventIndex = 0;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventSequenceTokenV2"/> class.
        /// </summary>
        /// <param name="seqNumber">The sequence number.</param>
        /// <param name="eventInd">The event index, for events which are part of a batch of events.</param>
        public EventSequenceTokenV2(long seqNumber, int eventInd)
        {
            SequenceNumber = seqNumber;
            EventIndex = eventInd;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventSequenceTokenV2"/> class.
        /// </summary>
        /// <remarks>
        /// This constructor is for serializer use only.
        /// </remarks>
        public EventSequenceTokenV2()
        {
        }

        /// <summary>
        /// Creates a sequence token for a specific event in the current batch
        /// </summary>
        /// <param name="eventInd">The event index.</param>
        /// <returns>A new sequence token.</returns>
        public EventSequenceTokenV2 CreateSequenceTokenForEvent(int eventInd)
        {
            return new EventSequenceTokenV2(SequenceNumber, eventInd);
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return Equals(obj as EventSequenceTokenV2);
        }

        /// <inheritdoc/>
        public override bool Equals(StreamSequenceToken other)
        {
            var token = other as EventSequenceTokenV2;
            return token != null && (token.SequenceNumber == SequenceNumber &&
                                     token.EventIndex == EventIndex);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            // why 397?
            return (EventIndex * 397) ^ SequenceNumber.GetHashCode();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[EventSequenceTokenV2: SeqNum={0}, EventIndex={1}]", SequenceNumber, EventIndex);
        }
    }
}
