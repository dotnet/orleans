using Orleans.Serialization.Buffers;
using System;
using System.Buffers;

namespace Orleans.Serialization.Serializers
{
    public interface IBaseCodec<T> : IBaseCodec where T : class
    {
        void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, T value) where TBufferWriter : IBufferWriter<byte>;
        void Deserialize<TInput>(ref Reader<TInput> reader, T value);
    }

    public interface IBaseCodec
    {
    }

    public interface IGeneralizedBaseCodec : IBaseCodec<object>
    {
        bool IsSupportedType(Type type);
    }

    public interface ISpecializableBaseCodec
    {
        bool IsSupportedType(Type type);
        IBaseCodec GetSpecializedCodec(Type type);
    }
}