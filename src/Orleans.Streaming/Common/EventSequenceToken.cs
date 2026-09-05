using System;
using System.Globalization;
using Orleans.Streams;
using Newtonsoft.Json;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Stream sequence token that tracks sequence number and event index
    /// </summary>
    /// <remarks>
    /// <see cref="EventSequenceToken"/> and <see cref="EventSequenceTokenV2"/> share a numeric
    /// position contract with subclasses which inherit their complete equality, ordering, and
    /// hashing implementations. This includes exact base tokens persisted by earlier event-token
    /// factories. Subclasses which override that contract define their own compatibility.
    /// </remarks>
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
        /// <returns>A token with the same concrete runtime type and position metadata, targeting the specified event.</returns>
        public virtual EventSequenceToken CreateSequenceTokenForEvent(int eventInd)
        {
            var result = (EventSequenceToken)MemberwiseClone();
            result.EventIndex = eventInd;
            return result;
        }

        internal virtual StreamSequenceToken NormalizeLegacyToken(StreamSequenceToken token) => token;

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is StreamSequenceToken token && Equals(token);
        }

        /// <inheritdoc />
        public override bool Equals(StreamSequenceToken? other)
        {
            return other is not null
                && EventSequenceTokenCompatibility.IsCompatibleNumericToken(this, other)
                && other.SequenceNumber == SequenceNumber
                && other.EventIndex == EventIndex;
        }

        /// <inheritdoc />
        public override int CompareTo(StreamSequenceToken? other)
        {
            if (other == null)
                return 1;

            if (!EventSequenceTokenCompatibility.IsCompatibleNumericToken(this, other))
                throw new ArgumentOutOfRangeException(nameof(other));

            int difference = SequenceNumber.CompareTo(other.SequenceNumber);
            return difference != 0 ? difference : EventIndex.CompareTo(other.EventIndex);
        }

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(SequenceNumber, EventIndex);

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[EventSequenceToken: SeqNum={0}, EventIndex={1}]", SequenceNumber, EventIndex);
        }
    }
}
