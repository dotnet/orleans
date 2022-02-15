using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="DateTime"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class DateTimeCodec : IFieldCodec<DateTime>
    {
        /// <summary>
        /// The codec field type
        /// </summary>
        public static readonly Type CodecFieldType = typeof(DateTime);

        /// <inheritdoc/>
        void IFieldCodec<DateTime>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, DateTime value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <inheritdoc/>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, DateTime value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed64);
            writer.WriteInt64(value.ToBinary());
        }

        /// <inheritdoc/>
        DateTime IFieldCodec<DateTime>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <inheritdoc/>
        public static DateTime ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            if (field.WireType != WireType.Fixed64)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            return DateTime.FromBinary(reader.ReadInt64());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.Fixed64} is supported for {nameof(DateTime)} fields. {field}");
    }

    /// <summary>
    /// Copier for <see cref="DateTime"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class DateTimeCopier : IDeepCopier<DateTime>
    {
        /// <inheritdoc/>
        public DateTime DeepCopy(DateTime input, CopyContext _) => input;
    }
}