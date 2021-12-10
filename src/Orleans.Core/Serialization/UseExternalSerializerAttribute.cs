using System;

namespace Orleans.Serialization
{
    /// <summary>
    /// Allows a type to specify the serializer type to use for this class in the event that no other serializer claims responsibility.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class EnableKeyedSerializerAttribute : Attribute
    {
        public EnableKeyedSerializerAttribute(Type serializerType)
        {
            SerializerType = serializerType ?? throw new ArgumentNullException(nameof(serializerType));
            if (!typeof(IExternalSerializer).IsAssignableFrom(serializerType)) throw new ArgumentException($"Type {serializerType} does not implement {typeof(IKeyedSerializer)}");
        }

        /// <summary>
        /// The serializer type to use for this class in the event that no other serializer claims responsibility.
        /// </summary>
        public Type SerializerType { get; set; }
    }
}
