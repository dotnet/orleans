
using System;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Stream position uniquely identifies the position of an event in a stream.
    /// If acquiring a stream position for a batch of events, the stream position will be of the first event in the batch.
    /// </summary>
    public class StreamPosition
    {
        public StreamPosition(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken)
        {
            if (streamIdentity == null)
            {
                throw new ArgumentNullException("streamIdentity");
            }
            if (sequenceToken == null)
            {
                throw new ArgumentNullException("sequenceToken");
            }
            StreamIdentity = streamIdentity;
            SequenceToken = sequenceToken;
        }
        public IStreamIdentity StreamIdentity { get; }
        public StreamSequenceToken SequenceToken { get; }

    }
}
