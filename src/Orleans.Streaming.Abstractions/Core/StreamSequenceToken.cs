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
        /// Gets the number of event batches in stream prior to this event batch
        /// </summary>
        public abstract long SequenceNumber { get; protected set;  }

        /// <summary>
        /// Gets the number of events in batch prior to this event
        /// </summary>
        public abstract int EventIndex { get; protected set; }

        /// <inheritdoc/>
        public abstract bool Equals(StreamSequenceToken other);

        /// <inheritdoc/>
        public abstract int CompareTo(StreamSequenceToken other);
    }

    /// <summary>
    /// Utilities for comparing <see cref="StreamSequenceToken"/> instances.
    /// </summary>
    public static class StreamSequenceTokenUtilities
    {
        /// <summary>
        /// Returns <see langword="true"/> if the first token is newer than the second token.
        /// </summary>
        /// <param name="me">The first token</param>
        /// <param name="other">The second token.</param>
        /// <returns><see langword="true" /> if the first token is newer than the second token, <see langword="false" /> otherwise.</returns>
        static public bool Newer(this StreamSequenceToken me, StreamSequenceToken other)
        {
            return me.CompareTo(other) > 0;
        }

        /// <summary>
        /// Returns <see langword="true"/> if the first token is older than the second token.
        /// </summary>
        /// <param name="me">The first token</param>
        /// <param name="other">The second token.</param>
        /// <returns><see langword="true" /> if the first token is older than the second token, <see langword="false" /> otherwise.</returns>
        static public bool Older(this StreamSequenceToken me, StreamSequenceToken other)
        {
            return me.CompareTo(other) < 0;
        }
    }
}
