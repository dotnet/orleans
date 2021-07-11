using System;
using System.IO;
using FASTER.core;
using Grains.Models;
using ProtoBuf;

namespace Grains
{
    /*
    public class ProtobufObjectSerializer<T> : IObjectSerializer<T>
    {
        private Stream stream;

        public ProtobufObjectSerializer()
        {
            Serializer.PrepareSerializer<T>();
        }

        #region Serialize

        public void BeginSerialize(Stream stream) => this.stream = stream;

        public void Serialize(ref T obj) => Serializer.SerializeWithLengthPrefix(stream, obj, PrefixStyle.Base128);

        public void EndSerialize() => stream.Dispose();

        #endregion Serialize

        #region Deserialize

        public void BeginDeserialize(Stream stream) => this.stream = stream;

        public void Deserialize(ref T obj) => Serializer.MergeWithLengthPrefix(stream, obj, PrefixStyle.Base128);

        public void EndDeserialize() => stream.Dispose();

        #endregion Deserialize
    }
    */

    public class LookupItemSerializer : BinaryObjectSerializer<LookupItem>
    {
        public override void Deserialize(ref LookupItem obj)
        {
            var key = reader.ReadInt32();
            var value = reader.ReadDecimal();
            var timestamp = DateTime.FromBinary(reader.ReadInt64());

            obj = new LookupItem(key, value, timestamp);
        }

        public override void Serialize(ref LookupItem obj)
        {
            writer.Write(obj.Key);
            writer.Write(obj.Value);
            writer.Write(obj.Timestamp.ToBinary());
        }
    }
}