using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Serialization
{
    internal class TypeSerializer
    {
        private readonly ConcurrentDictionary<Type, TypeKey> typeCache = new ConcurrentDictionary<Type, TypeKey>();

        private readonly ConcurrentDictionary<TypeKey, Type> typeKeyCache =
            new ConcurrentDictionary<TypeKey, Type>(new TypeKey.Comparer());

        private readonly Func<Type, TypeKey> getTypeKey;

        public TypeSerializer()
        {
            this.getTypeKey = type => new TypeKey(Encoding.UTF8.GetBytes(this.GetNameFromType(type)));
        }

        public static TypeKey ReadTypeKey(BinaryTokenStreamReader reader)
        {
            var hashCode = reader.ReadInt();
            var count = reader.ReadUShort();
            var typeName = reader.ReadBytes(count);
            return new TypeKey(hashCode, typeName);
        }

        public static void WriteTypeKey(TypeKey key, BinaryTokenStreamWriter writer)
        {
            writer.Write(key.HashCode);
            writer.Write((ushort)key.TypeName.Length);
            writer.Write(key.TypeName);
        }

        public void WriteType(Type actualType, Type expectedType, BinaryTokenStreamWriter writer)
        {
            if (actualType == expectedType)
            {
                writer.Write((byte)SerializationTokenType.ExpectedType);
            }
            else
            {
                writer.Write((byte)SerializationTokenType.NamedType);
                this.WriteNamedType(actualType, writer);
            }
        }

        public Type ReadType(SerializationTokenType token, BinaryTokenStreamReader reader, Type expectedType)
        {
            switch (token)
            {
                case SerializationTokenType.ExpectedType:
                    return expectedType;
                case SerializationTokenType.NamedType:
                    return this.ReadNamedType(reader);
                default:
                    throw new NotSupportedException($"{nameof(SerializationTokenType)} of {token} is not supported.");
            }
        }

        public Type ReadNamedType(BinaryTokenStreamReader reader)
        {
            var key = ReadTypeKey(reader);
            return this.GetTypeFromTypeKey(key, throwOnError: true);
        }

        public Type GetTypeFromTypeKey(TypeKey key, bool throwOnError = true)
        {
            Type result;
            if (!this.typeKeyCache.TryGetValue(key, out result))
            {
                result = this.GetTypeFromName(Encoding.UTF8.GetString(key.TypeName), throwOnError: throwOnError);
                if (result != null)
                {
                    this.typeKeyCache[key] = result;
                }
            }

            return result;
        }

        public void WriteNamedType(Type type, BinaryTokenStreamWriter writer)
        {
            var key = this.typeCache.GetOrAdd(type, this.getTypeKey);
            WriteTypeKey(key, writer);
        }

        /// <summary>
        /// The method used by this instance to retrieve a type from an assembly-qualified name.
        /// </summary>
        /// <param name="assemblyQualifiedTypeName">The type name.</param>
        /// <param name="throwOnError">Whether or not to throw if the type could not be loaded.</param>
        /// <returns>The type, or <see langword="null"/> if the type could not be loaded.</returns>
        internal virtual Type GetTypeFromName(string assemblyQualifiedTypeName, bool throwOnError)
            => Type.GetType(assemblyQualifiedTypeName, throwOnError: throwOnError);

        /// <summary>
        /// The method used by this instance to retrieve an assembly-qualified name from a type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The assembly-qualified name of <paramref name="type"/>.</returns>
        internal virtual string GetNameFromType(Type type) => type.AssemblyQualifiedName;

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

            public TypeKey(string typeName) : this(Encoding.UTF8.GetBytes(typeName)) { }

            public string GetTypeName() => Encoding.UTF8.GetString(this.TypeName);

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
    }
}