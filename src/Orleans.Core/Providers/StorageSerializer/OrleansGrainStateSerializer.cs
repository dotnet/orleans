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
        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        private static List<string> supportedTags => new List<string> { WellKnownSerializerTag.Binary };

        private readonly Serializer serializer;

        public List<string> SupportedTags => supportedTags;

        public OrleansGrainStorageSerializer(Serializer serializer)
        {
            this.serializer = serializer;
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public string Serialize<T>(T value, out BinaryData output)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            this.serializer.Serialize(value, buffer);
            output = new BinaryData(buffer.WrittenMemory);
            return WellKnownSerializerTag.Binary;
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public T Deserialize<T>(BinaryData input, string tag)
        {
            if (!tag.Equals(WellKnownSerializerTag.Binary, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ArgumentException($"Unsupported tag '{tag}'", nameof(tag));
            }

            return this.serializer.Deserialize<T>(input.ToMemory());
        }
    }
}
