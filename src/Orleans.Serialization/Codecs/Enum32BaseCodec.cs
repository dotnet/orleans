using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Serialization.Codecs
{
    public class Enum32BaseCodec<T> : IFieldCodec<T>, IDeepCopier<T> where T : Enum
    {
        public static readonly Type CodecFieldType = typeof(T);

        void IFieldCodec<T>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);
            var holder = new HolderStruct
            {
                Value = value
            };

            var intValue = Unsafe.As<T, int>(ref holder.Value);
            writer.WriteInt32(intValue);
        }

        T IFieldCodec<T>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        public static T ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            if (field.WireType != WireType.Fixed32)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var intValue = reader.ReadInt32();
            var result = Unsafe.As<int, T>(ref intValue);
            return result;
        }

        public T DeepCopy(T input, CopyContext context) => input;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.Fixed32} is supported for {typeof(T).GetType()} fields. {field}");

        [StructLayout(LayoutKind.Sequential)]
        private struct HolderStruct
        {
            public T Value;
            public int Padding;
        }
    }

    [RegisterSerializer]
    [RegisterCopier]
    public sealed class DateTimeKindCodec : Enum32BaseCodec<DateTimeKind>
    {
    }

    [RegisterSerializer]
    [RegisterCopier]
    public sealed class DayOfWeekCodec : Enum32BaseCodec<DayOfWeek>
    {
    }
}