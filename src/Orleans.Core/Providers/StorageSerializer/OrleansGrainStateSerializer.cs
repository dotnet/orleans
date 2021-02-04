using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain storage serializer that uses the Orleans <see cref="Serializer"/>
    /// </summary>
    public class OrleansGrainStorageSerializer : IGrainStorageSerializer
    {
        private readonly Serializer serializer;

        public OrleansGrainStorageSerializer(Serializer serializer)
        {
            this.serializer = serializer;
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public BinaryData Serialize<T>(T value)
        {
            var buffer = new ArrayBufferWriter<byte>();
            this.serializer.Serialize(value, buffer);
            return new BinaryData(buffer.WrittenMemory);
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public T Deserialize<T>(BinaryData input)
        {
            return this.serializer.Deserialize<T>(input.ToMemory());
        }
    }
}
