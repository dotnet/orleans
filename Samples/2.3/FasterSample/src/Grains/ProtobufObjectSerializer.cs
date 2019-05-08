using System.IO;
using FASTER.core;
using ProtoBuf;

namespace Grains
{
    public class ProtobufObjectSerializer<T> : IObjectSerializer<T>
    {
        private Stream stream;

        public void BeginDeserialize(Stream stream) => this.stream = stream;

        public void BeginSerialize(Stream stream) => this.stream = stream;

        public void Deserialize(ref T obj) => obj = Serializer.Deserialize<T>(stream);

        public void EndDeserialize()
        {
        }

        public void EndSerialize()
        {
        }

        public void Serialize(ref T obj) => Serializer.Serialize(stream, obj);
    }
}