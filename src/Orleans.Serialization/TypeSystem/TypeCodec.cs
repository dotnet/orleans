using Orleans.Serialization.Buffers;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Functionality for serializing and deserializing types.
    /// </summary>
    public sealed class TypeCodec
    {
        private const byte Version1 = 1;
        private readonly ConcurrentDictionary<Type, TypeKey> _typeCache = new ConcurrentDictionary<Type, TypeKey>();
        private readonly ConcurrentDictionary<int, (TypeKey Key, Type Type)> _typeKeyCache = new ConcurrentDictionary<int, (TypeKey, Type)>();
        private readonly TypeConverter _typeConverter;
        private readonly Func<Type, TypeKey> _getTypeKey;

        /// <summary>
        /// Initializes a new instance of the <see cref="TypeCodec"/> class.
        /// </summary>
        /// <param name="typeConverter">The type converter.</param>
        public TypeCodec(TypeConverter typeConverter)
        {
            _typeConverter = typeConverter;
            _getTypeKey = type => new TypeKey(Encoding.UTF8.GetBytes(_typeConverter.Format(type)));
        }

        /// <summary>
        /// Writes a type with a length-prefix.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="type">The type.</param>
        public void WriteLengthPrefixed<TBufferWriter>(ref Writer<TBufferWriter> writer, Type type) where TBufferWriter : IBufferWriter<byte>
        {
            var key = _typeCache.GetOrAdd(type, _getTypeKey);
            writer.WriteVarUInt32((uint)key.TypeName.Length);
            writer.Write(key.TypeName);
        }

        /// <summary>
        /// Writes a type.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="type">The type.</param>
        public void WriteEncodedType<TBufferWriter>(ref Writer<TBufferWriter> writer, Type type) where TBufferWriter : IBufferWriter<byte>
        {
            var key = _typeCache.GetOrAdd(type, _getTypeKey);
            writer.WriteByte(Version1);
            writer.WriteInt32(key.HashCode);
            writer.WriteVarUInt32((uint)key.TypeName.Length);
            writer.Write(key.TypeName);
        }

        /// <summary>
        /// Reads a type.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if a type was successfully read, <see langword="false" /> otherwise.</returns>
        public unsafe bool TryRead<TInput>(ref Reader<TInput> reader, [NotNullWhen(true)] out Type type)
        {
            var version = reader.ReadByte();
            if (version != Version1)
            {
                ThrowUnsupportedVersion(version);
            }

            var hashCode = reader.ReadInt32();
            var count = (int)reader.ReadVarUInt32();

            if (!reader.TryReadBytes(count, out var typeName))
            {
                typeName = reader.ReadBytes((uint)count);
            }

            // Search through 
            var candidateHashCode = hashCode;
            while (_typeKeyCache.TryGetValue(candidateHashCode, out var entry))
            {
                var existingKey = entry.Key;
                if (existingKey.HashCode != hashCode)
                {
                    break;
                }

                if (existingKey.TypeName.AsSpan().SequenceEqual(typeName))
                {
                    type = entry.Type;
                    return true;
                }

                // Try the next entry.
                ++candidateHashCode;
            }

            // Allocate a string for the type name.
            string typeNameString;
            fixed (byte* typeNameBytes = typeName)
            {
                typeNameString = Encoding.UTF8.GetString(typeNameBytes, typeName.Length);
            }

            _ = _typeConverter.TryParse(typeNameString, out type);
            if (type is object)
            {
                var key = new TypeKey(hashCode, typeName.ToArray());
                while (!_typeKeyCache.TryAdd(candidateHashCode++, (key, type)))
                {
                    // Insert the type at the first available position.
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads a length-prefixed type.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>Type.</returns>
        public unsafe Type ReadLengthPrefixed<TInput>(ref Reader<TInput> reader)
        {
            var count = (int)reader.ReadVarUInt32();

            if (!reader.TryReadBytes(count, out var typeName))
            {
                typeName = reader.ReadBytes((uint)count);
            }

            // Allocate a string for the type name.
            string typeNameString;
            fixed (byte* typeNameBytes = typeName)
            {
                typeNameString = Encoding.UTF8.GetString(typeNameBytes, typeName.Length);
            }

            var type = _typeConverter.Parse(typeNameString);
            return type;
        }

        /// <summary>
        /// Tries to read a type for diagnostics purposes.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="type">The type.</param>
        /// <param name="typeString">The type name as a string.</param>
        /// <returns><see langword="true" /> if a type was successfully read, <see langword="false" /> otherwise.</returns>
        public unsafe bool TryReadForAnalysis<TInput>(ref Reader<TInput> reader, [NotNullWhen(true)] out Type type, out string typeString)
        {
            var version = reader.ReadByte();
            var hashCode = reader.ReadInt32();
            var count = (int)reader.ReadVarUInt32();

            if (!reader.TryReadBytes(count, out var typeName))
            {
                typeName = reader.ReadBytes((uint)count);
            }

            // Allocate a string for the type name.
            string typeNameString;
            fixed (byte* typeNameBytes = typeName)
            {
                typeNameString = Encoding.UTF8.GetString(typeNameBytes, count);
            }

            _ = _typeConverter.TryParse(typeNameString, out type);
            var key = new TypeKey(hashCode, typeName.ToArray());
            typeString = key.ToString();
            return type is object; 
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedVersion(byte version)
        {
            throw new NotSupportedException($"Type encoding version {version} is not supported");
        }

        /// <summary>
        /// Represents a named type for the purposes of serialization.
        /// </summary>
        internal readonly struct TypeKey
        {
            public readonly int HashCode;

            public readonly byte[] TypeName;

            public TypeKey(int hashCode, byte[] key)
            {
                HashCode = hashCode;
                TypeName = key;
            }

            public TypeKey(byte[] key)
            {
                HashCode = unchecked((int)JenkinsHash.ComputeHash(key));
                TypeName = key;
            }

            public bool Equals(in TypeKey other)
            {
                if (HashCode != other.HashCode)
                {
                    return false;
                }

                var a = TypeName;
                var b = other.TypeName;
                return ReferenceEquals(a, b) || a.AsSpan().SequenceEqual(b);
            }

            public override bool Equals(object obj) => obj is TypeKey key && Equals(key);

            public override int GetHashCode() => HashCode;

            public override string ToString() => $"TypeName \"{Encoding.UTF8.GetString(TypeName)}\" (hash {HashCode:X8})";
        }
    }
}