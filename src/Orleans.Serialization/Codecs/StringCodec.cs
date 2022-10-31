using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="string"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class StringCodec : IFieldCodec<string>
    {
        /// <summary>
        /// The codec field type
        /// </summary>
        public static readonly Type CodecFieldType = typeof(string);

        /// <inheritdoc />
        string IFieldCodec<string>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<string, TInput>(ref reader, field);
            }

            field.EnsureWireType(WireType.LengthPrefixed);
            var length = reader.ReadVarUInt32();

            string result;
#if NETCOREAPP3_1_OR_GREATER
            if (reader.TryReadBytes((int) length, out var span))
            {
                result = Encoding.UTF8.GetString(span);
            }
            else      
#endif
            {
                var bytes = reader.ReadBytes(length);
                result = Encoding.UTF8.GetString(bytes);
            }

            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <inheritdoc />
        void IFieldCodec<string>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, string value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, string value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
#if NETCOREAPP3_1_OR_GREATER
            var numBytes = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarUInt32((uint)numBytes);
            if (numBytes < 512)
            {
                writer.EnsureContiguous(numBytes);
            }

            var currentSpan = writer.WritableSpan;

            // If there is enough room in the current span for the encoded data,
            // then encode directly into the output buffer.
            if (numBytes <= currentSpan.Length)
            {
                Encoding.UTF8.GetBytes(value, currentSpan);
                writer.AdvanceSpan(numBytes);
            }
            else
            {
                // Note: there is room for optimization here.
                Span<byte> bytes = Encoding.UTF8.GetBytes(value);
                writer.Write(bytes);
            }
#else
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.WriteVarUInt32((uint)bytes.Length);
            writer.Write(bytes);
#endif

        }
    }
}