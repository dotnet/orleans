using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Handle representing stream sequence number/token.
    /// Consumer may subscribe to the stream while specifying the start of the subscription sequence token.
    /// That means that the stream infrastructure will deliver stream events starting from this sequence token.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public abstract class StreamSequenceToken : IEquatable<StreamSequenceToken>, IComparable<StreamSequenceToken>
    {
        /// <summary>
        /// Number of event batches in stream prior to this event batch
        /// </summary>
        [Id(1)]
        public abstract long SequenceNumber { get; protected set;  }

        /// <summary>
        /// Number of events in batch prior to this event
        /// </summary>
        [Id(2)]
        public abstract int EventIndex { get; protected set; }

        public abstract bool Equals(StreamSequenceToken other);

        public abstract int CompareTo(StreamSequenceToken other);
    }

    public static class StreamSequenceTokenUtilities
    {
        static public bool Newer(this StreamSequenceToken me, StreamSequenceToken other)
        {
            return me.CompareTo(other) > 0;
        }

        static public bool Older(this StreamSequenceToken me, StreamSequenceToken other)
        {
            return me.CompareTo(other) < 0;
        }
    }
}
