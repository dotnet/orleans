using Orleans.Concurrency;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Serialization.ProtobufNet
{
    public class ProtobufTypeCacheItem
    {
        public readonly bool IsSupported;
        public readonly bool IsImmutable;

        public ProtobufTypeCacheItem(Type type)
        {
            IsSupported = Attribute.GetCustomAttribute(type, typeof(ProtoContractAttribute), false) != null;
            IsImmutable = Attribute.GetCustomAttribute(type, typeof(ImmutableAttribute), false) != null;
        }
    }
}
