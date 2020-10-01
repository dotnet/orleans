using System;
using System.Buffers;
using System.IO;
using Orleans.Serialization;

namespace Orleans.Storage
{
    public interface IGrainStateSerializer
    {
        void Serialize(Stream s, Type t, object value);

        object Deserialize(Type expected, Stream s);
    }

    public static class GrainStateSerializerExtentions
    {
        public static ReadOnlyMemory<byte> Serialize(this IGrainStateSerializer serializer, Type t, object value)
        {
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, t, value);
                return stream.GetBuffer();
            }
        }

        public static object Deserialize(this IGrainStateSerializer serializer, Type expected, byte[] input)
        {
            using (var stream = new MemoryStream(input))
            {
                return serializer.Deserialize(expected, stream);
            }
        }
    }

    internal class OrleansGrainStateSerializer : IGrainStateSerializer
    {
        private readonly SerializationManager serializationManager;

        public OrleansGrainStateSerializer(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        public void Serialize(Stream s, Type t, object value)
        {
            var buffer = this.serializationManager.SerializeToByteArray(value);
            s.Write(buffer, 0, buffer.Length);
        }

        public object Deserialize(Type expected, Stream s)
        {
            if (s is MemoryStream memStream && memStream.TryGetBuffer(out var buffer))
            {
                return Deserialize(buffer);
            }
            else
            {
                // We have to copy to get the buffer
                using (var tmpStream = new MemoryStream())
                {
                    s.CopyTo(tmpStream);
                    tmpStream.TryGetBuffer(out var buffer2);
                    return Deserialize(buffer2);
                }
            }
        }

        private object Deserialize(ArraySegment<byte> buffer)
        {
            var reader = new BinaryTokenStreamReader(buffer);
            return this.serializationManager.Deserialize(reader);
        }
    }
}
