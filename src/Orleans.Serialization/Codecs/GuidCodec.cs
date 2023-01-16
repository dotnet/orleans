using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Guid"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class GuidCodec : IFieldCodec<Guid>
    {
        private const int Width = 16;

        void IFieldCodec<Guid>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Guid value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Guid), WireType.LengthPrefixed);
            writer.WriteVarUInt7(Width);
            WriteRaw(ref writer, value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Guid value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.LengthPrefixed);
            writer.WriteVarUInt7(Width);
            WriteRaw(ref writer, value);
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

            field.EnsureWireType(WireType.LengthPrefixed);

            uint length = reader.ReadVarUInt32();
            if (length != Width)
            {
                throw new UnexpectedLengthPrefixValueException(nameof(Guid), Width, length);
            }

            return ReadRaw(ref reader);
        }

        /// <summary>
        /// Writes the raw GUID content.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, Guid value) where TBufferWriter : IBufferWriter<byte>
        {
            if (BitConverter.IsLittleEndian)
            {
#if NET7_0_OR_GREATER
                writer.Write(MemoryMarshal.AsBytes(new Span<Guid>(ref value)));
#else
                writer.EnsureContiguous(Width);
                if (value.TryWriteBytes(writer.WritableSpan))
                {
                    writer.AdvanceSpan(Width);
                    return;
                }
                writer.Write(value.ToByteArray());
#endif
            }
            else
            {
                writer.EnsureContiguous(Width);
                var done = value.TryWriteBytes(writer.WritableSpan);
                Debug.Assert(done);
                writer.AdvanceSpan(Width);
            }
        }

        /// <summary>
        /// Reads the raw GUID content.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid ReadRaw<TInput>(ref Reader<TInput> reader)
        {
#if NET7_0_OR_GREATER
            Unsafe.SkipInit(out Guid res);
            var bytes = MemoryMarshal.AsBytes(new Span<Guid>(ref res));
            reader.ReadBytes(bytes);

            if (BitConverter.IsLittleEndian)
                return res;

            return new Guid(bytes);
#else
            if (reader.TryReadBytes(Width, out var readOnly))
            {
                return new Guid(readOnly);
            }

            Span<byte> bytes = stackalloc byte[Width];
            reader.ReadBytes(bytes);

            return new Guid(bytes);
#endif
        }
    }
}