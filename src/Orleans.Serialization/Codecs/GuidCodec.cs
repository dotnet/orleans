using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Guid"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class GuidCodec : IFieldCodec<Guid>
    {
        /// <summary>
        /// The codec field type
        /// </summary>
        public static readonly Type CodecFieldType = typeof(Guid);
        private const int Width = 16;

        /// <inheritdoc/>
        void IFieldCodec<Guid>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Guid value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Guid value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Guid), WireType.LengthPrefixed);
            writer.WriteVarUInt32(Width);
#if NETCOREAPP3_1_OR_GREATER
            writer.EnsureContiguous(Width);
            if (value.TryWriteBytes(writer.WritableSpan))
            {
                writer.AdvanceSpan(Width);
                return;
            }
#endif
            writer.Write(value.ToByteArray());
        }

        /// <inheritdoc/>
        Guid IFieldCodec<Guid>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);
                
        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
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

#if NETCOREAPP3_1_OR_GREATER
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

        public static void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, Guid value) where TBufferWriter : IBufferWriter<byte>
        {
#if NETCOREAPP3_1_OR_GREATER
            writer.EnsureContiguous(Width);
            if (value.TryWriteBytes(writer.WritableSpan))
            {
                writer.AdvanceSpan(Width);
                return;
            }
#endif
            writer.Write(value.ToByteArray());
        }

        public static Guid ReadRaw<TInput>(ref Reader<TInput> reader)
        {
#if NETCOREAPP3_1_OR_GREATER
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

    /// <summary>
    /// Copier for <see cref="Guid"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class GuidCopier : IDeepCopier<Guid>
    {
        /// <inheritdoc/>
        public Guid DeepCopy(Guid input, CopyContext _) => input;
    }
}