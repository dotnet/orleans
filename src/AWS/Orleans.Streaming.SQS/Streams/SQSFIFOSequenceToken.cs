using System;
using System.Globalization;
using Newtonsoft.Json;
using Orleans;
using Orleans.Streams;

namespace OrleansAWSUtils.Streams
{
    /// <summary>
    /// Stream sequence token that tracks sequence number and event index
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class SQSFIFOSequenceToken : StreamSequenceToken
    {
        /// <summary>
        /// Gets the number of event batches in stream prior to this event batch
        /// </summary>
        [Id(0)]
        [JsonProperty]
        public UInt128 SqsSequenceNumber { get; set; }

        /// <summary>
        /// Gets the number of event batches in stream prior to this event batch
        /// </summary>
        public override long SequenceNumber
        {
            get => throw new NotSupportedException();
            protected set => throw new NotSupportedException();
        }

        /// <summary>
        /// Gets the number of events in batch prior to this event
        /// </summary>
        [Id(1)]
        [JsonProperty]
        public override int EventIndex { get; protected set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SQSFIFOSequenceToken"/> class.
        /// </summary>
        /// <param name="seqNumber">The sequence number.</param>
        public SQSFIFOSequenceToken(UInt128 seqNumber)
        {
            SqsSequenceNumber = seqNumber;
            EventIndex = 0;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SQSFIFOSequenceToken"/> class.
        /// </summary>
        /// <param name="seqNumber">The sequence number.</param>
        /// <param name="eventInd">The event index, for events which are part of a batch of events.</param>
        public SQSFIFOSequenceToken(UInt128 seqNumber, int eventInd)
        {
            SqsSequenceNumber = seqNumber;
            EventIndex = eventInd;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SQSFIFOSequenceToken"/> class.
        /// </summary>
        /// <remarks>
        /// This constructor is for serializer use only.
        /// </remarks>
        public SQSFIFOSequenceToken()
        {
        }

        /// <summary>
        /// Creates a sequence token for a specific event in the current batch
        /// </summary>
        /// <param name="eventInd">The event index.</param>
        /// <returns>A new sequence token.</returns>
        public SQSFIFOSequenceToken CreateSequenceTokenForEvent(int eventInd)
        {
            return new SQSFIFOSequenceToken(SqsSequenceNumber, eventInd);
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return Equals(obj as SQSFIFOSequenceToken);
        }

        /// <inheritdoc/>
        public override bool Equals(StreamSequenceToken other)
        {
            var token = other as SQSFIFOSequenceToken;
            return token != null && (token.SqsSequenceNumber == SqsSequenceNumber &&
                                     token.EventIndex == EventIndex);
        }

        /// <inheritdoc/>
        public override int CompareTo(StreamSequenceToken other)
        {
            if (other == null)
                return 1;

            var token = other as SQSFIFOSequenceToken;
            if (token == null)
                throw new ArgumentOutOfRangeException(nameof(other));

            int difference = SqsSequenceNumber.CompareTo(token.SqsSequenceNumber);
            return difference != 0 ? difference : EventIndex.CompareTo(token.EventIndex);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            // why 397?
            return (EventIndex * 397) ^ SqsSequenceNumber.GetHashCode();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[SQSFIFOSequenceToken: SeqNum={0}, EventIndex={1}]", SqsSequenceNumber, EventIndex);
        }
    }
}
