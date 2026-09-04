using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using System;
using System.Globalization;
using System.Numerics;

namespace Orleans.Streaming.Kinesis
{
    [Serializable]
    [GenerateSerializer]
    internal sealed class KinesisSequenceToken : PartitionedStreamSequenceToken
    {
        [NonSerialized]
        private BigInteger? _numericShardSequence;

        /// <summary>
        /// Initializes a new instance of the <see cref="KinesisSequenceToken" /> class.
        /// </summary>
        /// <param name="shardSequence">Kinesis offset within the shard (partition) from which this message came.</param>
        /// <param name="sequenceNumber">Receiver-generated sequenceNumber for this message.</param>
        /// <param name="eventIndex">Index into a batch of events, if multiple events were delivered within a single Kinesis record.</param>
        public KinesisSequenceToken(string shardSequence, long sequenceNumber, int eventIndex)
            : this(null, null, shardSequence, sequenceNumber, eventIndex)
        {
        }

        internal KinesisSequenceToken(
            string shardId,
            string shardSequence,
            long sequenceNumber,
            int eventIndex)
            : this(null, shardId, shardSequence, sequenceNumber, eventIndex)
        {
        }

        [JsonConstructor]
        internal KinesisSequenceToken(
            string? streamName,
            string? shardId,
            string shardSequence,
            long sequenceNumber,
            int eventIndex)
            : base(streamName, shardId, shardSequence, sequenceNumber, eventIndex)
        {
            StreamName = streamName;
            ShardId = shardId;
            ShardSequence = shardSequence ?? throw new ArgumentNullException(nameof(shardSequence));
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
        public string ShardSequence { get; } = null!;

        /// <summary>
        /// Gets the Kinesis shard which produced this position.
        /// </summary>
        [Id(1)]
        [JsonProperty]
        public string? ShardId { get; }

        /// <summary>
        /// Gets the Kinesis stream which contains this position.
        /// </summary>
        [Id(2)]
        [JsonProperty]
        public string? StreamName { get; }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is StreamSequenceToken token && Equals(token);

        /// <inheritdoc />
        public override bool Equals(StreamSequenceToken? other)
        {
            return other is KinesisSequenceToken token
                && string.Equals(StreamName, token.StreamName, StringComparison.Ordinal)
                && string.Equals(ShardId, token.ShardId, StringComparison.Ordinal)
                && NumericShardSequence.Equals(token.NumericShardSequence)
                && EventIndex == token.EventIndex;
        }

        /// <inheritdoc />
        public override int CompareTo(StreamSequenceToken? other)
        {
            if (other is null)
            {
                return 1;
            }

            if (other is not KinesisSequenceToken token)
            {
                throw new ArgumentOutOfRangeException(nameof(other));
            }

            if (!string.Equals(StreamName, token.StreamName, StringComparison.Ordinal)
                || !string.Equals(ShardId, token.ShardId, StringComparison.Ordinal))
            {
                throw new ArgumentOutOfRangeException(nameof(other));
            }

            var difference = CompareShardSequences(ShardSequence, token.ShardSequence);
            return difference != 0 ? difference : EventIndex.CompareTo(token.EventIndex);
        }

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(StreamName, ShardId, NumericShardSequence, EventIndex);

        /// <summary>Returns a string that represents the current object.</summary>
        /// <returns>A string that represents the current object.</returns>
        /// <filterpriority>2</filterpriority>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "KinesisSequenceToken(StreamName: {0}, ShardId: {1}, ShardSequence: {2}, SequenceNumber: {3}, EventIndex: {4})", StreamName, ShardId, ShardSequence, SequenceNumber, EventIndex);
        }

        private BigInteger NumericShardSequence
            => _numericShardSequence ??= BigInteger.Parse(
                ShardSequence,
                NumberStyles.None,
                CultureInfo.InvariantCulture);

        internal static int CompareShardSequences(string left, string right)
        {
            var leftStart = 0;
            while (leftStart < left.Length && left[leftStart] == '0')
            {
                leftStart++;
            }

            var rightStart = 0;
            while (rightStart < right.Length && right[rightStart] == '0')
            {
                rightStart++;
            }

            var lengthComparison = (left.Length - leftStart).CompareTo(right.Length - rightStart);
            return lengthComparison != 0
                ? lengthComparison
                : left.AsSpan(leftStart).SequenceCompareTo(right.AsSpan(rightStart));
        }
    }
}
