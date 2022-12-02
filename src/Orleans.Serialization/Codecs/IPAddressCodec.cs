using System;
using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="IPAddress"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class IPAddressCodec : IFieldCodec<IPAddress>, IDerivedTypeCodec
    {
        IPAddress IFieldCodec<IPAddress>.ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static IPAddress ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field)
        {
            if (field.IsReference)
            {
                return ReferenceCodec.ReadReference<IPAddress, TInput>(ref reader, field);
            }

            field.EnsureWireType(WireType.LengthPrefixed);
            var result = ReadRaw(ref reader);
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <summary>
        /// Reads the raw length prefixed IP address value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IPAddress ReadRaw<TInput>(ref Buffers.Reader<TInput> reader)
        {
            var length = reader.ReadVarUInt32();
#if NET5_0_OR_GREATER
            if (reader.TryReadBytes((int)length, out var bytes))
                return new(bytes);
#endif
            return new(reader.ReadBytes(length));
        }

        void IFieldCodec<IPAddress>.WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPAddress value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, typeof(IPAddress), value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(IPAddress), WireType.LengthPrefixed);
            WriteRaw(ref writer, value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, IPAddress value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceFieldExpected(ref writer, fieldIdDelta, value))
            {
                return;
            }

            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.LengthPrefixed);
            WriteRaw(ref writer, value);
        }

        /// <summary>
        /// Writes the raw length prefixed IP address value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRaw<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, IPAddress value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.EnsureContiguous(1 + 16);
            var span = writer.WritableSpan;
            if (!value.TryWriteBytes(span.Slice(1), out var length)) ThrowNotSupported();
            span[0] = (byte)(length * 2 + 1); // VarInt length
            writer.AdvanceSpan(1 + length);
        }

        private static void ThrowNotSupported() => throw new NotSupportedException();
    }

    [RegisterCopier]
    internal sealed class IPAddressCopier : ShallowCopier<IPAddress>, IDerivedTypeCopier { }
}
