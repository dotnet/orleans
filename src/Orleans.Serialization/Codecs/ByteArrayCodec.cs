using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="byte"/> arrays.
    /// </summary>
    [RegisterSerializer]
    public sealed class ByteArrayCodec : TypedCodecBase<byte[], ByteArrayCodec>, IFieldCodec<byte[]>
    {
        /// <summary>
        /// The codec field type
        /// </summary>
        private static readonly Type CodecFieldType = typeof(byte[]);

        /// <inheritdoc/>
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

            if (field.WireType != WireType.LengthPrefixed)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var length = reader.ReadVarUInt32();
            var result = reader.ReadBytes(length);
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <inheritdoc/>
        void IFieldCodec<byte[]>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, byte[] value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, byte[] value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)value.Length);
            writer.Write(value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.LengthPrefixed} is supported for byte[] fields. {field}");
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
    public sealed class ReadOnlyMemoryOfByteCodec : TypedCodecBase<ReadOnlyMemory<byte>, ReadOnlyMemoryOfByteCodec>, IFieldCodec<ReadOnlyMemory<byte>>
    {
        /// <summary>
        /// The codec field type
        /// </summary>
        private static readonly Type CodecFieldType = typeof(ReadOnlyMemory<byte>);

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

            if (field.WireType != WireType.LengthPrefixed)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var length = reader.ReadVarUInt32();
            var result = reader.ReadBytes(length);
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <inheritdoc/>
        void IFieldCodec<ReadOnlyMemory<byte>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ReadOnlyMemory<byte> value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ReadOnlyMemory<byte> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)value.Length);
            writer.Write(value.Span);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.LengthPrefixed} is supported for ReadOnlyMemory<byte> fields. {field}");
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

            var result = new byte[input.Length];
            input.CopyTo(result.AsMemory());
            return result;
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

            var result = new byte[input.Count];
            input.AsSpan().CopyTo(result.AsSpan());
            return new ArraySegment<byte>(result);
        }
    }

    /// <summary>
    /// Serializer for <see cref="Memory{Byte}"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class MemoryOfByteCodec : TypedCodecBase<Memory<byte>, MemoryOfByteCodec>, IFieldCodec<Memory<byte>>
    {
        private static readonly Type CodecFieldType = typeof(Memory<byte>);

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

            if (field.WireType != WireType.LengthPrefixed)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var length = reader.ReadVarUInt32();
            var result = reader.ReadBytes(length);
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <inheritdoc/>
        void IFieldCodec<Memory<byte>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Memory<byte> value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Memory<byte> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)value.Length);
            writer.Write(value.Span);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.LengthPrefixed} is supported for ReadOnlyMemory<byte> fields. {field}");
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

            var result = new byte[input.Length];
            input.CopyTo(result.AsMemory());
            return result;
        }
    }
}