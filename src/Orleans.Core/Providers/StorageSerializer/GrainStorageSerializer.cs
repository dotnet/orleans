using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain storage serializer that use a primary serializer and can deserialize from other serializers
    /// </summary>
    public class GrainStorageSerializer : IGrainStorageSerializer
    {
        private readonly IGrainStorageSerializer serializer;
        private readonly Dictionary<string, IGrainStorageSerializer> deserializers = new Dictionary<string, IGrainStorageSerializer>();

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public List<string> SupportedTags => this.deserializers.Keys.ToList();

        public GrainStorageSerializer(IGrainStorageSerializer serializer, params IGrainStorageSerializer[] fallbackDeserializers)
        {
            this.serializer = serializer;
            InsertDeserializer(serializer);

            foreach (var deserializer in fallbackDeserializers)
            {
                InsertDeserializer(deserializer);
            }

            void InsertDeserializer(IGrainStorageSerializer deserializer)
            {
                foreach (var tag in deserializer.SupportedTags)
                {
                    this.deserializers[tag] = deserializer;
                }
            }
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public string Serialize<T>(T value, out BinaryData output) => this.serializer.Serialize(value, out output);

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public T Deserialize<T>(BinaryData input, string tag)
        {
            if (!this.deserializers.TryGetValue(tag, out var deserializer))
            {
                throw new ArgumentException($"Unsupported tag '{tag}'", nameof(tag));
            }

            return deserializer.Deserialize<T>( input, tag);
        }
    }
}
