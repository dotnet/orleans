using System;
using System.Runtime.CompilerServices;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Buffers
{
    public ref partial struct Writer<TBufferWriter>
    {
        /// <summary>
        /// Writes the start object tag.
        /// </summary>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="actualType">The actual type.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteStartObject(uint fieldId, Type expectedType, Type actualType) => WriteFieldHeader(fieldId, expectedType, actualType, WireType.TagDelimited);

        /// <summary>
        /// Writes the end object tag.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteEndObject() => WriteByte((byte)WireType.Extended | (byte)ExtendedWireType.EndTagDelimited);

        /// <summary>
        /// Writes the end base tag.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteEndBase() => WriteByte((byte)WireType.Extended | (byte)ExtendedWireType.EndBaseFields);
    }
}