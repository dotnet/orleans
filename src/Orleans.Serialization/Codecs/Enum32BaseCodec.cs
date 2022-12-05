using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for enum types with a 32-bit base.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <seealso cref="Orleans.Serialization.Codecs.IFieldCodec{T}" />
    public abstract class Enum32BaseCodec<T> : IFieldCodec<T> where T : unmanaged, Enum
    {
        private readonly Type CodecFieldType = typeof(T);

        /// <inheritdoc/>
        public unsafe void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T value) where TBufferWriter : IBufferWriter<byte>
        {
            HolderStruct holder;
            holder.Value = value;
            var intValue = *(int*)&holder;
            Int32Codec.WriteField(ref writer, fieldIdDelta, expectedType, intValue, CodecFieldType);
        }

        /// <inheritdoc/>
        public unsafe T ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            var intValue = Int32Codec.ReadValue(ref reader, field);
            return *(T*)&intValue;
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
    internal sealed class DateTimeKindCodec : Enum32BaseCodec<DateTimeKind> { }

    /// <summary>
    /// Serializer and copier for <see cref="DayOfWeek"/>.
    /// </summary>
    [RegisterSerializer]
    internal sealed class DayOfWeekCodec : Enum32BaseCodec<DayOfWeek> { }
}