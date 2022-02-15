using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for enum types with a 32-bit base.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <seealso cref="Orleans.Serialization.Codecs.IFieldCodec{T}" />
    /// <seealso cref="Orleans.Serialization.Cloning.IDeepCopier{T}" />
    public class Enum32BaseCodec<T> : IFieldCodec<T>, IDeepCopier<T> where T : Enum
    {
        /// <summary>
        /// The codec field type
        /// </summary>
        public static readonly Type CodecFieldType = typeof(T);

        /// <inheritdoc/>
        void IFieldCodec<T>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        T IFieldCodec<T>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

    /// <summary>
    /// Serializer and copier for <see cref="DateTimeKind"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class DateTimeKindCodec : Enum32BaseCodec<DateTimeKind>
    {
    }

    /// <summary>
    /// Serializer and copier for <see cref="DayOfWeek"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class DayOfWeekCodec : Enum32BaseCodec<DayOfWeek>
    {
    }
}