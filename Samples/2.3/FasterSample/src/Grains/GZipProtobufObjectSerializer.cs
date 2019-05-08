using System.IO;
using System.IO.Compression;
using FASTER.core;
using ProtoBuf;

namespace Grains
{
    public class GZipProtobufObjectSerializer<T> : IObjectSerializer<T>
    {
        private GZipStream stream;

        public void BeginDeserialize(Stream stream) => this.stream = new GZipStream(stream, CompressionMode.Decompress);

        public void BeginSerialize(Stream stream) => this.stream = new GZipStream(stream, CompressionMode.Compress);

        public void Deserialize(ref T obj) => obj = Serializer.Deserialize<T>(stream);

        public void EndDeserialize()
        {
        }

        public void EndSerialize() => stream.Flush();

        public void Serialize(ref T obj) => Serializer.Serialize(stream, obj);
    }
}