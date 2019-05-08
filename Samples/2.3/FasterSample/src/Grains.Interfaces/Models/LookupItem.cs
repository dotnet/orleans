using System;
using Orleans.Concurrency;
using ProtoBuf;

namespace Grains.Models
{
    [Immutable]
    [ProtoContract]
    public class LookupItem
    {
        public LookupItem()
        {
            Key = 0;
            Value = 0;
            Timestamp = DateTime.MinValue;
        }

        public LookupItem(int key, decimal value, DateTime timestamp)
        {
            Key = key;
            Value = value;
            Timestamp = timestamp;
        }

        [ProtoMember(1)]
        public int Key { get; protected set; }

        [ProtoMember(2)]
        public decimal Value { get; protected set; }

        [ProtoMember(3)]
        public DateTime Timestamp { get; protected set; }
    }
}