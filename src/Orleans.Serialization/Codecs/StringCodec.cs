using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="string"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class StringCodec : IFieldCodec<string>
    {
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
            var result = ReadRaw(ref reader, reader.ReadVarUInt32());
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <summary>
        /// Reads the raw string content.
        /// </summary>
        /// <param name="numBytes">Encoded string length in bytes.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ReadRaw<TInput>(ref Reader<TInput> reader, uint numBytes)
        {
            if (reader.TryReadBytes((int)numBytes, out var span))
                return Encoding.UTF8.GetString(span);

            return ReadMultiSegment(ref reader, numBytes);
        }

        private static string ReadMultiSegment<TInput>(ref Reader<TInput> reader, uint numBytes)
        {
            var array = ArrayPool<byte>.Shared.Rent((int)numBytes);
            var span = array.AsSpan(0, (int)numBytes);
            reader.ReadBytes(span);
            var res = Encoding.UTF8.GetString(span);
            ArrayPool<byte>.Shared.Return(array);
            return res;
        }

        void IFieldCodec<string>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, string value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
                return;

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(string), WireType.LengthPrefixed);
            var numBytes = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarUInt32((uint)numBytes);
            WriteRaw(ref writer, value, numBytes);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, string value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceFieldExpected(ref writer, fieldIdDelta, value))
            {
                return;
            }

            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.LengthPrefixed);
            var numBytes = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarUInt32((uint)numBytes);
            WriteRaw(ref writer, value, numBytes);
        }

        /// <summary>
        /// Writes the raw string content.
        /// </summary>
        /// <param name="value">String to be encoded.</param>
        /// <param name="numBytes">Encoded string length in bytes.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, string value, int numBytes) where TBufferWriter : IBufferWriter<byte>
        {
            if (numBytes < 512)
            {
                writer.EnsureContiguous(numBytes);
            }

            var currentSpan = writer.WritableSpan;

            // If there is enough room in the current span for the encoded data,
            // then encode directly into the output buffer.
            if (numBytes <= currentSpan.Length)
            {
                writer.AdvanceSpan(Encoding.UTF8.GetBytes(value, currentSpan));
            }
            else
            {
                WriteMultiSegment(ref writer, value, numBytes);
            }
        }

        private static void WriteMultiSegment<TBufferWriter>(ref Writer<TBufferWriter> writer, string value, int remainingBytes) where TBufferWriter : IBufferWriter<byte>
        {
            var encoder = Encoding.UTF8.GetEncoder();
            var input = value.AsSpan();

            while (true)
            {
                encoder.Convert(input, writer.WritableSpan, true, out var charsUsed, out var bytesWritten, out var completed);
                writer.AdvanceSpan(bytesWritten);

                if (completed)
                {
                    Debug.Assert(charsUsed == input.Length && bytesWritten == remainingBytes);
                    break;
                }

                remainingBytes -= bytesWritten;
                input = input[charsUsed..];

                writer.Allocate(Math.Min(remainingBytes, Writer<TBufferWriter>.MaxMultiSegmentSizeHint));
            }
        }
    }
}