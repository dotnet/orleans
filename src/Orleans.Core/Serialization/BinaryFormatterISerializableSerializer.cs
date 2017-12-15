using System;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    /// <summary>
    /// A wrapper around <see cref="BinaryFormatterSerializer"/> which only serializes ISerializable types.
    /// </summary>
    internal class BinaryFormatterISerializableSerializer : IKeyedSerializer
    {
        private static readonly Type SerializableType = typeof(ISerializable);
        
        private readonly BinaryFormatterSerializer serializer;

        public BinaryFormatterISerializableSerializer(BinaryFormatterSerializer serializer)
        {
            this.serializer = serializer;
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
    }
}