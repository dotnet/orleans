
using System;
using System.Globalization;
using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Location of a message within an EventHub partition
    /// </summary>
    public interface IEventHubPartitionLocation
    {
        /// <summary>
        /// Offset of the message within an EventHub partition
        /// </summary>
        string EventHubOffset { get; }

        /// <summary>
        /// EventHub sequence id of the message
        /// </summary>
        long SequenceNumber { get; }
    }

    /// <summary>
    /// Event Hub messages consist of a batch of application layer events, so EventHub tokens contain three pieces of information.
    /// EventHubOffset - this is a unique value per partition that is used to start reading from this message in the partition.
    /// SequenceNumber - EventHub sequence numbers are unique ordered message IDs for messages within a partition.  
    ///   The SequenceNumber is required for uniqueness and ordering of EventHub messages within a partition.
    /// event Index - Since each EventHub message may contain more than one application layer event, this value
    ///   indicates which application layer event this token is for, within an EventHub message.  It is required for uniqueness
    ///   and ordering of application layer events within an EventHub message.
    /// </summary>
    /// <remarks>
    /// Event Hub token versions and subclasses which inherit their complete equality, ordering,
    /// and hashing contract compare using the Event Hubs sequence number and event index.
    /// During recovery, Orleans interprets exact <see cref="EventSequenceToken"/> positions from
    /// the earlier inherited event-token factory in this provider's sequence-number space.
    /// Delivered event tokens preserve their concrete type and Event Hubs offset.
    /// </remarks>
    [Serializable]
    [GenerateSerializer]
    public class EventHubSequenceToken : EventSequenceToken, IEventHubPartitionLocation
    {
        /// <summary>
        /// Offset of the message within an EventHub partition
        /// </summary>
        [Id(0)]
        [JsonProperty]
        public string EventHubOffset { get; } = null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventHubSequenceToken" /> class.
        /// </summary>
        /// <param name="eventHubOffset">EventHub offset within the partition from which this message came.</param>
        /// <param name="sequenceNumber">EventHub sequenceNumber for this message.</param>
        /// <param name="eventIndex">Index into a batch of events, if multiple events were delivered within a single EventHub message.</param>
        public EventHubSequenceToken(string eventHubOffset, long sequenceNumber, int eventIndex)
            : base(sequenceNumber, eventIndex)
        {
            EventHubOffset = eventHubOffset;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventHubSequenceToken" /> class.
        /// </summary>
        /// <remarks>
        /// This constructor is exposed for serializer use only.
        /// </remarks>
        public EventHubSequenceToken() : base()
        {
        }

        internal override StreamSequenceToken NormalizeLegacyToken(StreamSequenceToken token)
        {
            if (token.GetType() == typeof(EventSequenceToken) && HasInheritedEventHubContract(this))
            {
                // The former inherited factory persisted the position without the offset.
                // Empty offsets also identify sequence-only tokens produced by EventHubDataAdapter.
                return new EventHubSequenceToken(string.Empty, token.SequenceNumber, token.EventIndex);
            }

            return token;
        }

        /// <inheritdoc />
        public override bool Equals(StreamSequenceToken? other)
        {
            return other is not null
                && IsCompatibleEventHubToken(other)
                && other.SequenceNumber == SequenceNumber
                && other.EventIndex == EventIndex;
        }

        /// <inheritdoc />
        public override int CompareTo(StreamSequenceToken? other)
        {
            if (other is null)
            {
                return 1;
            }

            if (!IsCompatibleEventHubToken(other))
            {
                throw new ArgumentOutOfRangeException(nameof(other));
            }

            var difference = SequenceNumber.CompareTo(other.SequenceNumber);
            return difference != 0 ? difference : EventIndex.CompareTo(other.EventIndex);
        }

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(SequenceNumber, EventIndex);

        /// <summary>Returns a string that represents the current object.</summary>
        /// <returns>A string that represents the current object.</returns>
        /// <filterpriority>2</filterpriority>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "EventHubSequenceToken(EventHubOffset: {0}, SequenceNumber: {1}, EventIndex: {2})", EventHubOffset, SequenceNumber, EventIndex);
        }

        private bool IsCompatibleEventHubToken(StreamSequenceToken? other)
        {
            if (other is null)
            {
                return false;
            }

            return GetType() == other.GetType()
                || other is EventHubSequenceToken
                    && HasInheritedEventHubContract(this)
                    && HasInheritedEventHubContract(other);
        }

        private static bool HasInheritedEventHubContract(StreamSequenceToken token)
            => EventSequenceTokenCompatibility.HasInheritedContract(token, typeof(EventHubSequenceToken), typeof(EventSequenceToken));
    }
}
