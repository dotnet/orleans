using System;
using System.Buffers;
using Orleans.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain storage serializer that uses the Orleans <see cref="Serializer"/>.
    /// </summary>
    public class OrleansGrainStorageSerializer : IGrainStorageSerializer
    {
        private readonly Serializer serializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansGrainStorageSerializer"/> class.
        /// </summary>
        /// <param name="serializer">The serializer.</param>
        public OrleansGrainStorageSerializer(Serializer serializer)
        {
            this.serializer = serializer;
        }

        /// <inheritdoc/>
        public BinaryData Serialize<T>(T value)
        {
            var buffer = new ArrayBufferWriter<byte>();
            serializer.Serialize(value, buffer);
            return new BinaryData(buffer.WrittenMemory);
        }

        /// <inheritdoc/>
        public T Deserialize<T>(BinaryData input) => serializer.Deserialize<T>(input.ToMemory());
    }
}
