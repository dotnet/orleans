using System;
using System.Buffers;
using Orleans.Serialization;

namespace Orleans.Storage
{
    public interface IGrainStateSerializer
    {
        void Serialize(Type t, object value, IBufferWriter<byte> output);

        object Deserialize(Type expected, ReadOnlySequence<byte> input);
    }

    public static class GrainStateSerializerExtensions
    {
        public static ReadOnlyMemory<byte> Serialize(this IGrainStateSerializer self, Type t, object value)
        {
#if NETCOREAPP
            var writer = new System.Buffers.ArrayBufferWriter<byte>();
#else
            var writer = new Orleans.Serialization.ArrayBufferWriter<byte>();
#endif
            self.Serialize(t, value, writer);
            return writer.WrittenMemory;
        }

        public static object Deserialize(this IGrainStateSerializer self, Type expected, ReadOnlyMemory<byte> input)
        {
            return self.Deserialize(expected, new ReadOnlySequence<byte>(input));
        }
    }
}
