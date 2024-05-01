using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using System;
using System.Globalization;

namespace Orleans.Streaming.Kinesis
{
    [Serializable]
    [GenerateSerializer]
    public class KinesisSequenceToken : EventSequenceTokenV2
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="KinesisSequenceToken" /> class.
        /// </summary>
        /// <param name="shardSequence">Kinesis offset within the shard (partition) from which this message came.</param>
        /// <param name="sequenceNumber">Receiver-generated sequenceNumber for this message.</param>
        /// <param name="eventIndex">Index into a batch of events, if multiple events were delivered within a single Kinesis record.</param>
        public KinesisSequenceToken(string shardSequence, long sequenceNumber, int eventIndex)
            : base(sequenceNumber, eventIndex)
        {
            ShardSequence = shardSequence;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="KinesisSequenceToken" /> class.
        /// </summary>
        /// <remarks>
        /// This constructor is exposed for serializer use only.
        /// </remarks>
        public KinesisSequenceToken() : base()
        {
        }

        /// <summary>
        /// Offset of the message within an Kinesis shard.
        /// </summary>
        [Id(0)]
        [JsonProperty]
        public string ShardSequence { get; }

        /// <summary>Returns a string that represents the current object.</summary>
        /// <returns>A string that represents the current object.</returns>
        /// <filterpriority>2</filterpriority>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "KinesisSequenceToken(ShardSequence: {0}, SequenceNumber: {1}, EventIndex: {2})", ShardSequence, SequenceNumber, EventIndex);
        }
    }
}
