using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class StringCodec : TypedCodecBase<string, StringCodec>, IFieldCodec<string>
    {
        public static readonly Type CodecFieldType = typeof(string);

        string IFieldCodec<string>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<string, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.LengthPrefixed)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var length = reader.ReadVarUInt32();

            string result;
#if NETCOREAPP
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

        void IFieldCodec<string>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, string value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, string value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
#if NETCOREAPP
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.LengthPrefixed} is supported for string fields. {field}");
    }

    [RegisterCopier]
    public sealed class StringCopier : IDeepCopier<string>
    {
        public static string DeepCopy(string input, CopyContext _) => input;
        string IDeepCopier<string>.DeepCopy(string input, CopyContext _) => input;
    }
}