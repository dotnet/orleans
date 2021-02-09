using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain storage serializer that uses the <see cref="SerializationManager"/>
    /// </summary>
    public class OrleansGrainStorageSerializer : IGrainStorageSerializer
    {
        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        private static List<string> supportedTags => new List<string> { WellKnownSerializerTag.Binary };

        private readonly SerializationManager serializationManager;

        public List<string> SupportedTags => supportedTags;

        public OrleansGrainStorageSerializer(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public string Serialize(Type t, object value, out BinaryData output)
        {
#if NETCOREAPP
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
#else
            var buffer = new Orleans.Serialization.ArrayBufferWriter<byte>();
#endif
            var writer = new BinaryTokenStreamWriter2<IBufferWriter<byte>>(buffer);
            this.serializationManager.Serialize(value, writer);
            writer.Commit();
            output = new BinaryData(buffer.WrittenMemory);
            return WellKnownSerializerTag.Binary;
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public object Deserialize(Type expected, BinaryData input, string tag)
        {
            if (!tag.Equals(WellKnownSerializerTag.Binary, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ArgumentException($"Unsupported tag '{tag}'", nameof(tag));
            }

            var reader = new BinaryTokenStreamReader2(new ReadOnlySequence<byte>(input.ToMemory()));
            return this.serializationManager.Deserialize(reader);
        }
    }
}
