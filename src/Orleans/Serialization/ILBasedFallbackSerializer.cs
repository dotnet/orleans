namespace Orleans.Serialization
{
    using System;
    using System.Reflection;

    using Orleans.Runtime;
    
    /// <summary>
    /// Fallback serializer to be used when other serializers are unavailable.
    /// </summary>
    public class IlBasedFallbackSerializer : IExternalSerializer
    {
        private readonly IlBasedSerializers serializers;
        
        public IlBasedFallbackSerializer()
        {
            this.serializers = new IlBasedSerializers();
        }

        /// <summary>
        /// Initializes the external serializer. Called once when the serialization manager creates 
        /// an instance of this type
        /// </summary>
        public void Initialize(Logger logger)
        {
        }

        /// <summary>
        /// Informs the serialization manager whether this serializer supports the type for serialization.
        /// </summary>
        /// <param name="t">The type of the item to be serialized</param>
        /// <returns>A value indicating whether the item can be serialized.</returns>
        public bool IsSupportedType(Type t) => IlBasedSerializerTypeChecker.IsSupportedType(t.GetTypeInfo());

        /// <summary>
        /// Tries to create a copy of source.
        /// </summary>
        /// <param name="source">The item to create a copy of</param>
        /// <returns>The copy</returns>
        public object DeepCopy(object source)
        {
            if (source == null) return null;
            return this.serializers.Get(source.GetType()).DeepCopy(source);
        }

        /// <summary>
        /// Tries to serialize an item.
        /// </summary>
        /// <param name="item">The instance of the object being serialized</param>
        /// <param name="writer">The writer used for serialization</param>
        /// <param name="expectedType">The type that the deserializer will expect</param>
        public void Serialize(object item, BinaryTokenStreamWriter writer, Type expectedType)
        {
            if (item == null)
            {
                writer.WriteNull();
                return;
            }

            var actualType = item.GetType();
            this.WriteType(actualType, expectedType, writer);
            this.serializers.Get(actualType).Serialize(item, writer, expectedType);
        }

        /// <summary>
        /// Tries to deserialize an item.
        /// </summary>
        /// <param name="reader">The reader used for binary deserialization</param>
        /// <param name="expectedType">The type that should be deserialzied</param>
        /// <returns>The deserialized object</returns>
        public object Deserialize(Type expectedType, BinaryTokenStreamReader reader)
        {
            var token = reader.ReadToken();
            if (token == SerializationTokenType.Null) return null;
            var actualType = this.ReadType(token, reader, expectedType);
            var methods = this.serializers.Get(actualType);
            var deserializer = methods.Deserialize;
            return deserializer(expectedType, reader);
        }
        
        private void WriteType(Type actualType, Type expectedType, BinaryTokenStreamWriter writer)
        {
            if (actualType == expectedType)
            {
                writer.Write((byte)SerializationTokenType.ExpectedType);
            }
            else
            {
                writer.Write((byte)SerializationTokenType.NamedType);
                writer.Write(actualType.AssemblyQualifiedName);
            }
        }

        private Type ReadType(SerializationTokenType token, BinaryTokenStreamReader reader, Type expectedType)
        {
            switch (token)
            {
                case SerializationTokenType.ExpectedType:
                    return expectedType;
                case SerializationTokenType.NamedType:
                    return Type.GetType(reader.ReadString(), throwOnError: true);
                default:
                    throw new NotSupportedException($"{nameof(SerializationTokenType)} of {token} is not supported.");
            }
        }
    }
}