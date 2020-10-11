
using System;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream position uniquely identifies the position of an event in a stream.
    /// If acquiring a stream position for a batch of events, the stream position will be of the first event in the batch.
    /// </summary>
    public class StreamPosition
    {
        /// <summary>
        /// Stream position consists of the stream identity and the sequence token
        /// </summary>
        /// <param name="streamId"></param>
        /// <param name="sequenceToken"></param>
        public StreamPosition(StreamId streamId, StreamSequenceToken sequenceToken)
        {
            if (sequenceToken == null)
            {
                throw new ArgumentNullException("sequenceToken");
            }
            StreamId = streamId;
            SequenceToken = sequenceToken;
        }
        /// <summary>
        /// Identity of the stream
        /// </summary>
        public StreamId StreamId { get; }

        /// <summary>
        /// Position in the stream
        /// </summary>
        public StreamSequenceToken SequenceToken { get; }
    }
}
