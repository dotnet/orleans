using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Handle representing stream sequence number/token.
    /// Consumer may subsribe to the stream while specifying the start of the subsription sequence token.
    /// That means that the stream infarstructure will deliver stream events starting from this sequence token.
    /// </summary>
    [Serializable]
    public abstract class StreamSequenceToken : IEquatable<StreamSequenceToken>, IComparable<StreamSequenceToken>
    {
        #region IEquatable<StreamSequenceToken> Members

        public abstract bool Equals(StreamSequenceToken other);

        #endregion

        #region IComparable<StreamSequenceToken> Members

        public abstract int CompareTo(StreamSequenceToken other);

        #endregion
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
