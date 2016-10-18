namespace Orleans.Serialization
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Reflection;

    using GeneratedSerializer = SerializationManager.SerializerMethods;

    public class IlBasedSerializers
    {
        /// <summary>
        /// The collection of generated serializers.
        /// </summary>
        private readonly ConcurrentDictionary<Type, GeneratedSerializer> serializers = new ConcurrentDictionary<Type, GeneratedSerializer>();

        /// <summary>
        /// The serializer generator.
        /// </summary>
        private readonly IlBasedSerializerGenerator generator = new IlBasedSerializerGenerator();

        /// <summary>
        /// The serializer used when a concrete type is not known.
        /// </summary>
        private readonly GeneratedSerializer genericSerializer;

        /// <summary>
        /// The serializer used for implementations of <see cref="Type"/>.
        /// </summary>
        private readonly GeneratedSerializer typeSerializer;

        public IlBasedSerializers()
        {
            // Configure the serializer to be used when a concrete type is not known.
            // The serializer will generate and register serializers for concrete types
            // as they are discovered.
            this.genericSerializer = new GeneratedSerializer(
                original =>
                {
                    if (original == null)
                    {
                        return null;
                    }

                    return this.GetAndRegister(original.GetType()).DeepCopy(original);
                },
                (original, writer, expected) =>
                {
                    if (original == null)
                    {
                        writer.WriteNull();
                        return;
                    }

                    this.GetAndRegister(original.GetType()).Serialize(original, writer, expected);
                },
                (expected, reader) => this.GetAndRegister(expected).Deserialize(expected, reader));

            this.typeSerializer = new GeneratedSerializer(
                original => original,
                (original, writer, expected) => { writer.Write(((Type)original).AssemblyQualifiedName); },
                (expected, reader) => Type.GetType(reader.ReadString(), throwOnError: true));
        }

        /// <summary>
        /// Gets a serializer for the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The serializer for the provided type.</returns>
        public GeneratedSerializer Get(Type type)
        {
            if (type.GetTypeInfo().IsGenericTypeDefinition) return this.genericSerializer;
            return this.serializers.GetOrAdd(type, this.GenerateSerializer);
        }

        /// <summary>
        /// Gets a serializer for the provided type, registers it, and returns it.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The serializer for the provided type.</returns>
        private GeneratedSerializer GetAndRegister(Type type)
        {
            var methods = this.Get(type);
            SerializationManager.Register(type, methods.DeepCopy, methods.Serialize, methods.Deserialize, forceOverride: true);
            return methods;
        }

        private GeneratedSerializer GenerateSerializer(Type type)
        {
            if (typeof(Type).IsAssignableFrom(type))
            {
                return this.typeSerializer;
            }

            Func<FieldInfo, bool> fieldFilter = null;
            if (typeof(Exception).IsAssignableFrom(type))
            {
                fieldFilter = this.ExceptionFieldFilter;
            }

            return this.generator.GenerateSerializer(type, fieldFilter);
        }
        
        private bool ExceptionFieldFilter(FieldInfo arg)
        {
            // Any field defined below Exception is acceptable.
            if (arg.DeclaringType != typeof(Exception)) return true;

            // Certain fields from the Exception base class are acceptable.
            return arg.FieldType == typeof(string) || arg.FieldType == typeof(Exception);
        }
    }
}