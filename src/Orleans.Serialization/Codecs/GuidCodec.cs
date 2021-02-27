using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class GuidCodec : IFieldCodec<Guid>
    {
        public static readonly Type CodecFieldType = typeof(Guid);
        private const int Width = 16;

        void IFieldCodec<Guid>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Guid value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Guid value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Guid), WireType.LengthPrefixed);
            writer.WriteVarUInt32(Width);
#if NETCOREAPP
            writer.EnsureContiguous(Width);
            if (value.TryWriteBytes(writer.WritableSpan))
            {
                writer.AdvanceSpan(Width);
                return;
            }
#endif
            writer.Write(value.ToByteArray());
        }

        Guid IFieldCodec<Guid>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        public static Guid ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);

            if (field.WireType != WireType.LengthPrefixed)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            uint length = reader.ReadVarUInt32();
            if (length != Width)
            {
                throw new UnexpectedLengthPrefixValueException(nameof(Guid), Width, length);
            }

#if NETCOREAPP
            if (reader.TryReadBytes(Width, out var readOnly))
            {
                return new Guid(readOnly);
            }

            Span<byte> bytes = stackalloc byte[Width];
            for (var i = 0; i < Width; i++)
            {
                bytes[i] = reader.ReadByte();
            }

            return new Guid(bytes);
#else
            return new Guid(reader.ReadBytes(Width));
#endif
        }

        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.LengthPrefixed} is supported for {nameof(Guid)} fields. {field}");
    }

    [RegisterCopier]
    public sealed class GuidCopier : IDeepCopier<Guid>
    {
        public Guid DeepCopy(Guid input, CopyContext _) => input;
    }
}