using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ByteArrayCodec : TypedCodecBase<byte[], ByteArrayCodec>, IFieldCodec<byte[]>
    {
        private static readonly Type CodecFieldType = typeof(byte[]);

        byte[] IFieldCodec<byte[]>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

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

        void IFieldCodec<byte[]>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, byte[] value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

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

    [RegisterCopier]
    public sealed class ByteArrayCopier : IDeepCopier<byte[]>
    {
        byte[] IDeepCopier<byte[]>.DeepCopy(byte[] input, CopyContext context) => DeepCopy(input, context);

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

    [RegisterSerializer]
    public sealed class ReadOnlyMemoryOfByteCodec : TypedCodecBase<ReadOnlyMemory<byte>, ReadOnlyMemoryOfByteCodec>, IFieldCodec<ReadOnlyMemory<byte>>
    {
        private static readonly Type CodecFieldType = typeof(ReadOnlyMemory<byte>);

        ReadOnlyMemory<byte> IFieldCodec<ReadOnlyMemory<byte>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

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

        void IFieldCodec<ReadOnlyMemory<byte>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ReadOnlyMemory<byte> value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

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

    [RegisterCopier]
    public sealed class ReadOnlyMemoryOfByteCopier : IDeepCopier<ReadOnlyMemory<byte>>
    {
        public ReadOnlyMemory<byte> DeepCopy(ReadOnlyMemory<byte> input, CopyContext _)
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

    [RegisterCopier]
    public sealed class ArraySegmentOfByteCopier : IDeepCopier<ArraySegment<byte>>
    {
        public ArraySegment<byte> DeepCopy(ArraySegment<byte> input, CopyContext _)
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

    [RegisterSerializer]
    public sealed class MemoryOfByteCodec : TypedCodecBase<Memory<byte>, MemoryOfByteCodec>, IFieldCodec<Memory<byte>>
    {
        private static readonly Type CodecFieldType = typeof(Memory<byte>);

        Memory<byte> IFieldCodec<Memory<byte>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

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

        void IFieldCodec<Memory<byte>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Memory<byte> value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

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

    [RegisterCopier]
    public sealed class MemoryOfByteCopier : IDeepCopier<Memory<byte>>
    {
        public Memory<byte> DeepCopy(Memory<byte> input, CopyContext _)
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