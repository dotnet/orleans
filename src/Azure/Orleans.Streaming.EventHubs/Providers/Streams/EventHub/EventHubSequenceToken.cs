
using System;
using System.Globalization;
using Orleans.Providers.Streams.Common;

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
    [Serializable]
    [GenerateSerializer]
    public class EventHubSequenceToken : EventSequenceToken, IEventHubPartitionLocation
    {
        /// <summary>
        /// Offset of the message within an EventHub partition
        /// </summary>
        [Id(0)]
        public string EventHubOffset { get; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="eventHubOffset">EventHub offset within the partition from which this message came.</param>
        /// <param name="sequenceNumber">EventHub sequenceNumber for this message.</param>
        /// <param name="eventIndex">Index into a batch of events, if multiple events were delivered within a single EventHub message.</param>
        public EventHubSequenceToken(string eventHubOffset, long sequenceNumber, int eventIndex)
            : base(sequenceNumber, eventIndex)
        {
            EventHubOffset = eventHubOffset;
        }

        /// <summary>Returns a string that represents the current object.</summary>
        /// <returns>A string that represents the current object.</returns>
        /// <filterpriority>2</filterpriority>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "EventHubSequenceToken(EventHubOffset: {0}, SequenceNumber: {1}, EventIndex: {2})", EventHubOffset, SequenceNumber, EventIndex);
        }
    }
}
