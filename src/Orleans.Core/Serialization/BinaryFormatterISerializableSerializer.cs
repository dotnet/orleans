using System;
using System.Runtime.Serialization;
using Microsoft.Extensions.Options;

namespace Orleans.Serialization
{
    /// <summary>
    /// Options for <see cref="BinaryFormatterISerializableSerializer"/>.
    /// </summary>
    public class BinaryFormatterISerializableSerializerOptions
    {
        /// <summary>
        /// Whether to use the <see cref="BinaryFormatterISerializableSerializer"/> serializer only as a fallback
        /// </summary>
        public bool IsFallbackOnly { get; set; } = true;
    }

    /// <summary>
    /// A wrapper around <see cref="BinaryFormatterSerializer"/> which only serializes ISerializable types.
    /// </summary>
    internal class BinaryFormatterISerializableSerializer : IKeyedSerializer
    {
        private static readonly Type SerializableType = typeof(ISerializable);
        
        private readonly BinaryFormatterSerializer serializer;
        private readonly BinaryFormatterISerializableSerializerOptions options;

        public BinaryFormatterISerializableSerializer(BinaryFormatterSerializer serializer, IOptions<BinaryFormatterISerializableSerializerOptions> options)
        {
            this.serializer = serializer;
            this.options = options.Value;
        }

        /// <inheritdoc />
        public bool IsSupportedType(Type itemType)
        {
            return itemType.IsSerializable && SerializableType.IsAssignableFrom(itemType) && DotNetSerializableUtilities.HasSerializationConstructor(itemType);
        }

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context) => this.serializer.DeepCopy(source, context);

        /// <inheritdoc />
        public void Serialize(object item, ISerializationContext context, Type expectedType) => this.serializer.Serialize(item, context, expectedType);

        /// <inheritdoc />
        public object Deserialize(Type expectedType, IDeserializationContext context) => this.serializer.Deserialize(expectedType, context);

        /// <inheritdoc />
        public KeyedSerializerId SerializerId => KeyedSerializerId.BinaryFormatterISerializable;

        /// <inheritdoc />
        public bool IsFallbackOnly => options.IsFallbackOnly;
    }
}