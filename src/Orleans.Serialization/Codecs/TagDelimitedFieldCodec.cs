using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Functionality for working with tag-delimited fields.
    /// </summary>
    public static class TagDelimitedFieldCodec
    {
        private static readonly byte EndObjectTag = new Tag
        {
            WireType = WireType.Extended,
            ExtendedWireType = ExtendedWireType.EndTagDelimited
        };

        private static readonly byte EndBaseFieldsTag = new Tag
        {
            WireType = WireType.Extended,
            ExtendedWireType = ExtendedWireType.EndBaseFields
        };

        /// <summary>
        /// Writes the start object tag.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="actualType">The actual type.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteStartObject<TBufferWriter>(
                    ref this Writer<TBufferWriter> writer,
                    uint fieldId,
                    Type expectedType,
                    Type actualType) where TBufferWriter : IBufferWriter<byte> => writer.WriteFieldHeader(fieldId, expectedType, actualType, WireType.TagDelimited);

        /// <summary>
        /// Writes the end object tag.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteEndObject<TBufferWriter>(this ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte> => writer.WriteByte((byte)EndObjectTag);

        /// <summary>
        /// Writes the end base tag.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteEndBase<TBufferWriter>(this ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte> => writer.WriteByte((byte)EndBaseFieldsTag);
    }
}