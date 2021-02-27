using System;
using ProtoBuf;

namespace Orleans.Serialization.ProtobufNet
{
    public class ProtobufTypeCacheItem
    {
        public readonly bool IsSupported;
        public readonly bool IsImmutable;

        public ProtobufTypeCacheItem(Type type)
        {
            IsSupported = type.IsDefined(typeof(ProtoContractAttribute), false);
            IsImmutable = type.IsDefined(typeof(ImmutableAttribute), false);
        }
    }
}
