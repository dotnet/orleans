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
    public class ILBasedSerializer : IExternalSerializer
    {
        /// <summary>
        /// The serializer generator.
        /// </summary>
        private readonly ILSerializerGenerator generator = new ILSerializerGenerator();

        /// <summary>
        /// The collection of generated serializers.
        /// </summary>
        private readonly ConcurrentDictionary<Type, SerializerBundle> serializers =
            new ConcurrentDictionary<Type, SerializerBundle>();

        private readonly ConcurrentDictionary<Type, TypeKey> typeCache = new ConcurrentDictionary<Type, TypeKey>();

        private readonly ConcurrentDictionary<TypeKey, Type> typeKeyCache =
            new ConcurrentDictionary<TypeKey, Type>(new TypeKey.Comparer());

        /// <summary>
        /// The serializer used when a concrete type is not known.
        /// </summary>
        private readonly SerializationManager.SerializerMethods thisSerializer;

        /// <summary>
        /// The serializer used for implementations of <see cref="Type"/>.
        /// </summary>
        private readonly SerializerBundle typeSerializer;

        private readonly Func<Type, SerializerBundle> generateSerializer;

        private readonly Func<FieldInfo, bool> exceptionFieldFilter;

        public ILBasedSerializer()
        {
            this.exceptionFieldFilter = ExceptionFieldFilter;

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
                    (original, context) => original,
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
            => this.serializers.ContainsKey(t) || ILSerializerGenerator.IsSupportedType(t.GetTypeInfo());

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context)
        {
            if (source == null) return null;
            Type type = source.GetType();
            return this.serializers.GetOrAdd(type, this.generateSerializer).Methods.DeepCopy(source, context);
        }

        /// <inheritdoc />
        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            if (item == null)
            {
                context.StreamWriter.WriteNull();
                return;
            }

            var actualType = item.GetType();
            this.WriteType(actualType, expectedType, context);
            this.serializers.GetOrAdd(actualType, this.generateSerializer).Methods.Serialize(item, context, expectedType);
        }

        /// <inheritdoc />
        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            var token = reader.ReadToken();
            if (token == SerializationTokenType.Null) return null;
            var actualType = this.ReadType(token, context, expectedType);
            return this.serializers.GetOrAdd(actualType, this.generateSerializer)
                       .Methods.Deserialize(expectedType, context);
        }

        private void WriteType(Type actualType, Type expectedType, ISerializationContext context)
        {
            if (actualType == expectedType)
            {
                context.StreamWriter.Write((byte)SerializationTokenType.ExpectedType);
            }
            else
            {
                context.StreamWriter.Write((byte)SerializationTokenType.NamedType);
                this.WriteNamedType(actualType, context);
            }
        }

        private Type ReadType(SerializationTokenType token, IDeserializationContext context, Type expectedType)
        {
            switch (token)
            {
                case SerializationTokenType.ExpectedType:
                    return expectedType;
                case SerializationTokenType.NamedType:
                    return this.ReadNamedType(context);
                default:
                    throw new NotSupportedException($"{nameof(SerializationTokenType)} of {token} is not supported.");
            }
        }

        private Type ReadNamedType(IDeserializationContext context)
        {
            var reader = context.StreamReader;
            var hashCode = reader.ReadInt();
            var count = reader.ReadUShort();
            var typeName = reader.ReadBytes(count);
            return this.typeKeyCache.GetOrAdd(
                new TypeKey(hashCode, typeName),
                k => Type.GetType(Encoding.UTF8.GetString(k.TypeName), throwOnError: true));
        }

        private void WriteNamedType(Type type, ISerializationContext context)
        {
            var writer = context.StreamWriter;
            var key = this.typeCache.GetOrAdd(type, t => new TypeKey(Encoding.UTF8.GetBytes(t.AssemblyQualifiedName)));
            writer.Write(key.HashCode);
            writer.Write((ushort)key.TypeName.Length);
            writer.Write(key.TypeName);
        }

        private SerializerBundle GenerateSerializer(Type type)
        {
            if (type.GetTypeInfo().IsGenericTypeDefinition) return new SerializerBundle(type, this.thisSerializer);

            if (typeof(Type).IsAssignableFrom(type))
            {
                return this.typeSerializer;
            }

            Func<FieldInfo, bool> fieldFilter = null;
            if (typeof(Exception).IsAssignableFrom(type))
            {
                fieldFilter = this.exceptionFieldFilter;
            }

            return new SerializerBundle(type, this.generator.GenerateSerializer(type, fieldFilter));
        }

        private static bool ExceptionFieldFilter(FieldInfo arg)
        {
            // Any field defined below Exception is acceptable.
            if (arg.DeclaringType != typeof(Exception)) return true;

            // Certain fields from the Exception base class are acceptable.
            return arg.FieldType == typeof(string) || arg.FieldType == typeof(Exception);
        }

        /// <summary>
        /// Represents a named type for the purposes of serialization.
        /// </summary>
        internal struct TypeKey
        {
            public readonly int HashCode;

            public readonly byte[] TypeName;

            public TypeKey(int hashCode, byte[] key)
            {
                this.HashCode = hashCode;
                this.TypeName = key;
            }

            public TypeKey(byte[] key)
            {
                this.HashCode = unchecked((int)JenkinsHash.ComputeHash(key));
                this.TypeName = key;
            }

            public bool Equals(TypeKey other)
            {
                if (this.HashCode != other.HashCode) return false;
                var a = this.TypeName;
                var b = other.TypeName;
                if (ReferenceEquals(a, b)) return true;
                if (a.Length != b.Length) return false;
                var length = a.Length;
                for (var i = 0; i < length; i++) if (a[i] != b[i]) return false;
                return true;
            }

            public override bool Equals(object obj)
            {
                return obj is TypeKey && this.Equals((TypeKey)obj);
            }

            public override int GetHashCode()
            {
                return this.HashCode;
            }

            internal class Comparer : IEqualityComparer<TypeKey>
            {
                public bool Equals(TypeKey x, TypeKey y)
                {
                    return x.Equals(y);
                }

                public int GetHashCode(TypeKey obj)
                {
                    return obj.HashCode;
                }
            }
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