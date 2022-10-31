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
    public abstract class Enum32BaseCodec<T> : IFieldCodec<T> where T : Enum
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
            field.EnsureWireType(WireType.Fixed32);
            var intValue = reader.ReadInt32();
            var result = Unsafe.As<int, T>(ref intValue);
            return result;
        }

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
    public sealed class DateTimeKindCodec : Enum32BaseCodec<DateTimeKind> { }

    /// <summary>
    /// Serializer and copier for <see cref="DayOfWeek"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class DayOfWeekCodec : Enum32BaseCodec<DayOfWeek> { }
}