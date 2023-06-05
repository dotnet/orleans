using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="byte"/> arrays.
    /// </summary>
    [RegisterSerializer]
    public sealed class ByteArrayCodec : IFieldCodec<byte[]>
    {
        byte[] IFieldCodec<byte[]>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static byte[] ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<byte[], TInput>(ref reader, field);
            }

            field.EnsureWireType(WireType.LengthPrefixed);
            var length = reader.ReadVarUInt32();
            var result = reader.ReadBytes(length);
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        void IFieldCodec<byte[]>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, byte[] value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(byte[]), WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)value.Length);
            writer.Write(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, byte[] value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceFieldExpected(ref writer, fieldIdDelta, value))
            {
                return;
            }

            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)value.Length);
            writer.Write(value);
        }
    }

    /// <summary>
    /// Copier for <see cref="byte"/> arrays.
    /// </summary>
    [RegisterCopier]
    public sealed class ByteArrayCopier : IDeepCopier<byte[]>
    {
        /// <inheritdoc/>
        byte[] IDeepCopier<byte[]>.DeepCopy(byte[] input, CopyContext context) => DeepCopy(input, context);

        /// <summary>
        /// Creates a deep copy of the provided input.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="context">The context.</param>
        /// <returns>A copy of <paramref name="input" />.</returns>
        public static byte[] DeepCopy(byte[] input, CopyContext context)
        {
            if (context.TryGetCopy<byte[]>(input, out var result))
            {
                return result;
            }

            result = new byte[input.Length];
            context.RecordCopy(input, result);
            input.CopyTo(result.AsSpan());
            return result;
        }
    }

    /// <summary>
    /// Serializer for <see cref="ReadOnlyMemory{Byte}"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class ReadOnlyMemoryOfByteCodec : IFieldCodec<ReadOnlyMemory<byte>>
    {
        /// <inheritdoc/>
        ReadOnlyMemory<byte> IFieldCodec<ReadOnlyMemory<byte>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static byte[] ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<byte[], TInput>(ref reader, field);
            }

            field.EnsureWireType(WireType.LengthPrefixed);
            var length = reader.ReadVarUInt32();
            var result = reader.ReadBytes(length);
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <inheritdoc/>
        void IFieldCodec<ReadOnlyMemory<byte>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ReadOnlyMemory<byte> value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, ReadOnlyMemory<byte> value) where TBufferWriter : IBufferWriter<byte>
            => WriteField(ref writer, fieldIdDelta, typeof(ReadOnlyMemory<byte>), value);

        private static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ReadOnlyMemory<byte> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(ReadOnlyMemory<byte>), WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)value.Length);
            writer.Write(value.Span);
        }
    }

    /// <summary>
    /// Copier for <see cref="ReadOnlyMemory{Byte}"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class ReadOnlyMemoryOfByteCopier : IDeepCopier<ReadOnlyMemory<byte>>
    {
        /// <inheritdoc/>
        ReadOnlyMemory<byte> IDeepCopier<ReadOnlyMemory<byte>>.DeepCopy(ReadOnlyMemory<byte> input, CopyContext _) => DeepCopy(input, _);

        /// <summary>
        /// Copies the input.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="copyContext">The copy context.</param>
        /// <returns>A copy of the input.</returns>
        public static ReadOnlyMemory<byte> DeepCopy(ReadOnlyMemory<byte> input, CopyContext copyContext)
        {
            if (input.IsEmpty)
            {
                return default;
            }

            return input.ToArray();
        }
    }

    /// <summary>
    /// Copier for <see cref="ArraySegment{Byte}"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class ArraySegmentOfByteCopier : IDeepCopier<ArraySegment<byte>>
    {
        /// <inheritdoc/>
        ArraySegment<byte> IDeepCopier<ArraySegment<byte>>.DeepCopy(ArraySegment<byte> input, CopyContext _) => DeepCopy(input, _);

        /// <summary>
        /// Copies the input.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="copyContext">The copy context.</param>
        /// <returns>A copy of the input.</returns>
        public static ArraySegment<byte> DeepCopy(ArraySegment<byte> input, CopyContext copyContext)
        {
            if (input.Array is null)
            {
                return default;
            }

            return input.AsSpan().ToArray();
        }
    }

    /// <summary>
    /// Serializer for <see cref="Memory{Byte}"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class MemoryOfByteCodec : IFieldCodec<Memory<byte>>
    {
        /// <inheritdoc/>
        Memory<byte> IFieldCodec<Memory<byte>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static Memory<byte> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<byte[], TInput>(ref reader, field);
            }

            field.EnsureWireType(WireType.LengthPrefixed);
            var length = reader.ReadVarUInt32();
            var result = reader.ReadBytes(length);
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <inheritdoc/>
        void IFieldCodec<Memory<byte>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Memory<byte> value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Memory<byte> value) where TBufferWriter : IBufferWriter<byte>
            => WriteField(ref writer, fieldIdDelta, typeof(Memory<byte>), value);

        private static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Memory<byte> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Memory<byte>), WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)value.Length);
            writer.Write(value.Span);
        }
    }

    /// <summary>
    /// Copier for <see cref="Memory{T}"/> of <see cref="byte"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class MemoryOfByteCopier : IDeepCopier<Memory<byte>>
    {
        /// <inheritdoc/>
        Memory<byte> IDeepCopier<Memory<byte>>.DeepCopy(Memory<byte> input, CopyContext _) => DeepCopy(input, _);

        /// <summary>
        /// Copies the input.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="copyContext">The copy context.</param>
        /// <returns>A copy of the input.</returns>
        public static Memory<byte> DeepCopy(Memory<byte> input, CopyContext copyContext)
        {
            if (input.IsEmpty)
            {
                return default;
            }

            return input.ToArray();
        }
    }

    /// <summary>
    /// Serializer for <see cref="PooledBuffer"/> instances.
    /// </summary>
    [RegisterSerializer]
    public sealed class PooledBufferCodec : IValueSerializer<PooledBuffer>
    {
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, scoped ref PooledBuffer value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteVarUInt32((uint)value.Length);
            foreach (var segment in value)
            {
                writer.Write(segment);
            }

            // Dispose of the value after sending it.
            // PooledBuffer is special in this sense.
            // Senders must not use the value after sending.
            // Receivers must dispose of the value after use.
            value.Reset();
            value = default;
        }

        public void Deserialize<TInput>(ref Reader<TInput> reader, scoped ref PooledBuffer value)
        {
            const int MaxSpanLength = 4096;
            var length = (int)reader.ReadVarUInt32();
            while (length > 0)
            {
                var copied = Math.Min(length, MaxSpanLength);
                var span = value.GetSpan(copied)[..copied];
                reader.ReadBytes(span);
                value.Advance(copied);
                length -= copied;
            }

            Debug.Assert(length == 0);
        }
    }

    /// <summary>
    /// Copier for <see cref="PooledBuffer"/> instances, which are assumed to be immutable.
    /// </summary>
    [RegisterCopier]
    public sealed class PooledBufferCopier : IDeepCopier<PooledBuffer>, IOptionalDeepCopier
    {
        public PooledBuffer DeepCopy(PooledBuffer input, CopyContext context) => input;
        public bool IsShallowCopyable() => true;
    }
}