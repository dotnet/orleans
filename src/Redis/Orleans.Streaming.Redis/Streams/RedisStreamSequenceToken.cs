using System;
using System.Globalization;
using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Streaming.Redis;

/// <summary>
/// Represents a position within a Redis stream entry and an event within that entry.
/// </summary>
[Serializable]
[GenerateSerializer]
internal sealed class RedisStreamSequenceToken : EventSequenceTokenV2
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RedisStreamSequenceToken"/> class.
    /// </summary>
    /// <param name="entryId">The Redis entry identifier.</param>
    /// <param name="sequenceNumber">The millisecond portion of the Redis entry identifier.</param>
    /// <param name="redisSequenceNumber">The per-millisecond Redis sequence number.</param>
    /// <param name="eventIndex">The index of the event in the Orleans batch.</param>
    public RedisStreamSequenceToken(string entryId, long sequenceNumber, long redisSequenceNumber, int eventIndex)
        : base(sequenceNumber, eventIndex)
    {
        EntryId = entryId;
        RedisSequenceNumber = redisSequenceNumber;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisStreamSequenceToken"/> class.
    /// </summary>
    public RedisStreamSequenceToken()
    {
    }

    /// <summary>
    /// Gets the Redis entry identifier.
    /// </summary>
    [Id(2)]
    [JsonProperty]
    public string EntryId { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the Redis per-millisecond sequence number.
    /// </summary>
    [Id(3)]
    [JsonProperty]
    public long RedisSequenceNumber { get; private set; }

    /// <summary>
    /// Creates a token for an event within the same Redis entry.
    /// </summary>
    /// <param name="eventIndex">The event index within the Orleans batch.</param>
    /// <returns>A token for the specified event.</returns>
    public new RedisStreamSequenceToken CreateSequenceTokenForEvent(int eventIndex) => new(EntryId, SequenceNumber, RedisSequenceNumber, eventIndex);

    /// <inheritdoc />
    public override bool Equals(StreamSequenceToken other)
    {
        return other is RedisStreamSequenceToken token
            && token.SequenceNumber == SequenceNumber
            && token.RedisSequenceNumber == RedisSequenceNumber
            && token.EventIndex == EventIndex
            && string.Equals(token.EntryId, EntryId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override int CompareTo(StreamSequenceToken other)
    {
        if (other is null)
        {
            return 1;
        }

        var difference = SequenceNumber.CompareTo(other.SequenceNumber);
        if (difference != 0)
        {
            return difference;
        }

        if (other is RedisStreamSequenceToken token)
        {
            difference = RedisSequenceNumber.CompareTo(token.RedisSequenceNumber);
            if (difference != 0)
            {
                return difference;
            }
        }

        return EventIndex.CompareTo(other.EventIndex);
    }

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(EntryId, SequenceNumber, RedisSequenceNumber, EventIndex);

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "[RedisStreamSequenceToken: EntryId={0}, SeqNum={1}, RedisSeqNum={2}, EventIndex={3}]",
            EntryId,
            SequenceNumber,
            RedisSequenceNumber,
            EventIndex);
    }
}
