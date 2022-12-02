using System;
using System.Buffers;
using System.Collections.Specialized;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="BitVector32"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class BitVector32Codec : IFieldCodec<BitVector32>
    {
        /// <inheritdoc />
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, BitVector32 value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(BitVector32), WireType.Fixed32);
            writer.WriteInt32(value.Data);  // BitVector32.Data gets the value of the BitVector32 as an Int32
        }

        /// <inheritdoc/>
        public BitVector32 ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            field.EnsureWireType(WireType.Fixed32);
            return new BitVector32(reader.ReadInt32());
        }
    }
}