
using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream position uniquely identifies the position of an event in a stream.
    /// If acquiring a stream position for a batch of events, the stream position will be of the first event in the batch.
    /// </summary>
    public struct StreamPosition
    {
        /// <summary>
        /// Stream position consists of the stream identity and the sequence token
        /// </summary>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        public StreamPosition(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken)
        {
            this.StreamIdentity = streamIdentity ?? throw new ArgumentNullException("streamIdentity");
            this.SequenceToken = sequenceToken ?? throw new ArgumentNullException("sequenceToken");
        }
        /// <summary>
        /// Identity of the stream
        /// </summary>
        public IStreamIdentity StreamIdentity { get; }

        /// <summary>
        /// Position in the stream
        /// </summary>
        public StreamSequenceToken SequenceToken { get; }
    }
}
