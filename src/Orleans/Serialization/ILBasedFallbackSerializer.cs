namespace Orleans.Serialization
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text;

    using Orleans.Runtime;
    
    /// <summary>
    /// Fallback serializer to be used when other serializers are unavailable.
    /// </summary>
    public class IlBasedFallbackSerializer : IExternalSerializer
    {
        /// <summary>
        /// The serializer generator.
        /// </summary>
        private readonly IlBasedSerializerGenerator generator = new IlBasedSerializerGenerator();

        /// <summary>
        /// The collection of generated serializers.
        /// </summary>
        private readonly ConcurrentDictionary<Type, SerializerBundle> serializers = new ConcurrentDictionary<Type, SerializerBundle>();

        /// <summary>
        /// The serializer used when a concrete type is not known.
        /// </summary>
        private readonly SerializationManager.SerializerMethods thisSerializer;

        private readonly Func<Type, SerializerBundle> generateSerializer;

        public IlBasedFallbackSerializer()
        {
            // Configure the serializer to be used when a concrete type is not known.
            // The serializer will generate and register serializers for concrete types
            // as they are discovered.
            this.thisSerializer = new SerializationManager.SerializerMethods(
                this.DeepCopy,
                this.Serialize,
                this.Deserialize);

            this.typeSerializer = new SerializerBundle(
                typeof(Type),
                new SerializationManager.SerializerMethods(
                    original => original,
                    (original, writer, expected) => { this.WriteNamedType((Type)original, writer); },
                    (expected, reader) => this.ReadNamedType(reader)));
            this.generateSerializer = this.GenerateSerializer;
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
        public bool IsSupportedType(Type t)
            => this.serializers.ContainsKey(t) || IlBasedSerializerGenerator.IsSupportedType(t.GetTypeInfo());

        /// <summary>
        /// Tries to create a copy of source.
        /// </summary>
        /// <param name="source">The item to create a copy of</param>
        /// <returns>The copy</returns>
        public object DeepCopy(object source)
        {
            if (source == null) return null;
            Type type = source.GetType();
            return this.serializers.GetOrAdd(type, this.generateSerializer).Methods.DeepCopy(source);
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
            this.serializers.GetOrAdd(actualType, this.generateSerializer).Methods.Serialize(item, writer, expectedType);
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
            return this.serializers.GetOrAdd(actualType, this.generateSerializer).Methods.Deserialize(expectedType, reader);
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

        private SerializerBundle GenerateSerializer(Type type)
        {
            if (type.GetTypeInfo().IsGenericTypeDefinition) return new SerializerBundle(type, this.thisSerializer);

            Func<FieldInfo, bool> fieldFilter = null;
            if (typeof(Exception).IsAssignableFrom(type))
            {
                fieldFilter = this.ExceptionFieldFilter;
            }

            return new SerializerBundle(type, this.generator.GenerateSerializer(type, fieldFilter));
        }

        private bool ExceptionFieldFilter(FieldInfo arg)
        {
            // Any field defined below Exception is acceptable.
            if (arg.DeclaringType != typeof(Exception)) return true;

            // Certain fields from the Exception base class are acceptable.
            return arg.FieldType == typeof(string) || arg.FieldType == typeof(Exception);
        }

        public class SerializerBundle
        {
            public readonly SerializationManager.SerializerMethods Methods;

            public readonly Type Type;

            public SerializerBundle(Type type, SerializationManager.SerializerMethods methods)
            {
                this.Type = type;
                this.Methods = methods;
            }
        }
    }
}