
using System;
using System.Globalization;
using Orleans.Providers.Streams.Common;

namespace Orleans.ServiceBus.Providers
{
    public interface IEventHubPartitionLocation
    {
        string EventHubOffset { get; }
        long SequenceNumber { get; }
    }

    /// <summary>
    /// Event Hub messages consist of a batch of application layer events, so EventHub tokens contain three pieces of information.
    /// EventHubOffset - this is a unique value per partition that is used to start reading from this message in the partition.
    /// SequenceNumber - EventHub sequence numbers are unique ordered message IDs for messages within a partition.  
    ///   The SequenceNumber is required for uniqueness and ordering of EventHub messages within a partition.
    /// event Index - Since each EventHub message may contain more than one application layer event, this value
    ///   indicates which application layer event this token is for, within an EventHub message.  It is required for uniqueness
    ///   and ordering of aplication layer events within an EventHub message.
    /// </summary>
    [Serializable]
    public class EventHubSequenceToken : EventSequenceToken, IEventHubPartitionLocation
    {
        public string EventHubOffset { get; private set; }

        public EventHubSequenceToken(string eventHubOffset, long sequenceNumber, int eventIndex)
            : base(sequenceNumber, eventIndex)
        {
            EventHubOffset = eventHubOffset;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "EventHubSequenceToken(EventHubOffset: {0}, SequenceNumber: {1}, EventIndex: {2})", EventHubOffset, SequenceNumber, EventIndex);
        }
    }
}
