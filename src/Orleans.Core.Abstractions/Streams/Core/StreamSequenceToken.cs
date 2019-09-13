using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Handle representing stream sequence number/token.
    /// Consumer may subscribe to the stream while specifying the start of the subscription sequence token.
    /// That means that the stream infrastructure will deliver stream events starting from this sequence token.
    /// </summary>
    [Serializable]
    public abstract class StreamSequenceToken : IEquatable<StreamSequenceToken>, IComparable<StreamSequenceToken>
    {
        public abstract byte[] SequenceToken { get; }

        public virtual bool Equals(StreamSequenceToken other) => new ReadOnlySpan<byte>(this.SequenceToken).SequenceEqual(other.SequenceToken);

        public virtual int CompareTo(StreamSequenceToken other) => new ReadOnlySpan<byte>(this.SequenceToken).SequenceCompareTo(other.SequenceToken);
    }

    public static class StreamSequenceTokenUtilities
    {
        static public bool Newer(this StreamSequenceToken me, StreamSequenceToken other) => me.CompareTo(other) > 0;
        static public bool Older(this StreamSequenceToken me, StreamSequenceToken other) => me.CompareTo(other) < 0;
    }
}
