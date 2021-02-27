using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class TimeSpanCodec : IFieldCodec<TimeSpan>
    {
        public static readonly Type CodecFieldType = typeof(TimeSpan);

        void IFieldCodec<TimeSpan>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TimeSpan value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TimeSpan value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed64);
            writer.WriteInt64(value.Ticks);
        }

        TimeSpan IFieldCodec<TimeSpan>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        public static TimeSpan ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            if (field.WireType != WireType.Fixed64)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            return TimeSpan.FromTicks(reader.ReadInt64());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.Fixed64} is supported for {nameof(TimeSpan)} fields. {field}");
    }

    [RegisterCopier]
    public sealed class TimeSpanCopier : IDeepCopier<TimeSpan>
    {
        public TimeSpan DeepCopy(TimeSpan input, CopyContext _) => input;
    }
}