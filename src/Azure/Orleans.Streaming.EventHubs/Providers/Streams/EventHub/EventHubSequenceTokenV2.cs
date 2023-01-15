using System;

namespace Orleans.Streaming.EventHubs
{
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
    public class EventHubSequenceTokenV2 : EventHubSequenceToken
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EventHubSequenceTokenV2" /> class.
        /// </summary>
        /// <param name="eventHubOffset">EventHub offset within the partition from which this message came.</param>
        /// <param name="sequenceNumber">EventHub sequenceNumber for this message.</param>
        /// <param name="eventIndex">Index into a batch of events, if multiple events were delivered within a single EventHub message.</param>
        public EventHubSequenceTokenV2(string eventHubOffset, long sequenceNumber, int eventIndex)
            : base(eventHubOffset, sequenceNumber, eventIndex)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventHubSequenceTokenV2" /> class.
        /// </summary>
        /// <remarks>
        /// This constructor is exposed for serializer use only.
        /// </remarks>
        public EventHubSequenceTokenV2() : base()
        {
        }
    }
}
