using System;
using System.Diagnostics.CodeAnalysis;
using Orleans.Concurrency;
using ProtoBuf;

namespace FasterSample.Grains
{
    /// <summary>
    /// Represents the frequency of some specified event.
    /// </summary>
    [Immutable]
    [ProtoContract(Surrogate = typeof(FrequencyItemSurrogate))]
    public class FrequencyItem
    {
        public FrequencyItem(Guid key, int count, DateTime timestamp)
        {
            Key = key;
            Count = count;
            Timestamp = timestamp;
        }

        public Guid Key { get; }

        public int Count { get; }

        public DateTime Timestamp { get; }

        public FrequencyItem Add(FrequencyItem item)
        {
            if (item is null) throw new ArgumentNullException(nameof(item));
            if (item.Key != Key) throw new InvalidOperationException();

            // accumulate the count
            var count = Count + item.Count;

            // keep the latest timestamp
            var timestamp = Timestamp > item.Timestamp ? Timestamp : item.Timestamp;

            return new FrequencyItem(Key, count, timestamp);
        }
    }

    /// <summary>
    /// Enables safe protobuf persistence of the immutable class <see cref="FrequencyItem"/>.
    /// </summary>
    [ProtoContract]
    [SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Not Required")]
    internal class FrequencyItemSurrogate
    {
        [ProtoMember(1)]
        public Guid Key { get; set; }

        [ProtoMember(2)]
        public int Count { get; set; }

        [ProtoMember(3)]
        public DateTime Timestamp { get; set; }

        public static implicit operator FrequencyItemSurrogate(FrequencyItem source)
        {
            return source is null ? null : new FrequencyItemSurrogate
            {
                Key = source.Key,
                Count = source.Count,
                Timestamp = source.Timestamp
            };
        }

        public static implicit operator FrequencyItem(FrequencyItemSurrogate surrogate)
        {
            return surrogate is null ? null : new FrequencyItem(
                surrogate.Key,
                surrogate.Count,
                surrogate.Timestamp);
        }
    }
}