using System;
using System.Globalization;
using Newtonsoft.Json;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Stream sequence token that tracks sequence number and event index
    /// </summary>
    /// <remarks>
    /// <see cref="EventSequenceTokenV2"/> and <see cref="EventSequenceToken"/> share a numeric
    /// position contract with subclasses which inherit their complete equality, ordering, and
    /// hashing implementations. This includes exact base tokens persisted by earlier event-token
    /// factories. Subclasses which override that contract define their own compatibility.
    /// </remarks>
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
        /// <returns>A token with the same concrete runtime type and position metadata, targeting the specified event.</returns>
        public virtual EventSequenceTokenV2 CreateSequenceTokenForEvent(int eventInd)
        {
            var result = (EventSequenceTokenV2)MemberwiseClone();
            result.EventIndex = eventInd;
            return result;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is StreamSequenceToken token && Equals(token);
        }

        /// <inheritdoc/>
        public override bool Equals(StreamSequenceToken? other)
        {
            return other is not null
                && EventSequenceTokenCompatibility.IsCompatibleNumericToken(this, other)
                && other.SequenceNumber == SequenceNumber
                && other.EventIndex == EventIndex;
        }

        /// <inheritdoc/>
        public override int CompareTo(StreamSequenceToken? other)
        {
            if (other == null)
                return 1;

            if (!EventSequenceTokenCompatibility.IsCompatibleNumericToken(this, other))
                throw new ArgumentOutOfRangeException(nameof(other));

            int difference = SequenceNumber.CompareTo(other.SequenceNumber);
            return difference != 0 ? difference : EventIndex.CompareTo(other.EventIndex);
        }

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(SequenceNumber, EventIndex);

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[EventSequenceTokenV2: SeqNum={0}, EventIndex={1}]", SequenceNumber, EventIndex);
        }
    }
}
