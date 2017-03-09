namespace Orleans.Streams
{
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