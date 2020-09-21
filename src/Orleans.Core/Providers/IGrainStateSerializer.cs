using System;
using Orleans.Serialization;

namespace Orleans.Storage
{
    public interface IGrainStateSerializer
    {
        ReadOnlyMemory<byte> Serialize(Type t, object value);

        object Deserialize(Type expected, ReadOnlyMemory<byte> value);
    }

    internal class OrleansGrainStateSerializer : IGrainStateSerializer
    {
        private readonly SerializationManager serializationManager;

        public OrleansGrainStateSerializer(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        public ReadOnlyMemory<byte> Serialize(Type t, object value) => this.serializationManager.SerializeToByteArray(value);

        public object Deserialize(Type expected, ReadOnlyMemory<byte> value) => this.serializationManager.DeserializeFromMemoryByte(typeof(object), value);
    }
}
