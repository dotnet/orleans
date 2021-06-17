using Orleans.Serialization.Buffers;
using System.Buffers;

namespace Orleans.Serialization.Serializers
{
    public interface IValueSerializer<T> : IValueSerializer where T : struct
    {
        void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, ref T value) where TBufferWriter : IBufferWriter<byte>;
        void Deserialize<TInput>(ref Reader<TInput> reader, ref T value);
    }

    public interface IValueSerializer
    {
    }
}