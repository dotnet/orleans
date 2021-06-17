using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Collections.Specialized;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class BitVector32Codec : IFieldCodec<BitVector32>
    {
        void IFieldCodec<BitVector32>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, BitVector32 value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, BitVector32 value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(BitVector32), WireType.Fixed32);
            writer.WriteInt32(value.Data);  // BitVector32.Data gets the value of the BitVector32 as an Int32
        }

        BitVector32 IFieldCodec<BitVector32>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        public static BitVector32 ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            if (field.WireType != WireType.Fixed32)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            return new BitVector32(reader.ReadInt32());
        }

        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.Fixed32} is supported for {nameof(BitVector32)} fields. {field}");
    }

    [RegisterCopier]
    public sealed class BitVector32Copier : IDeepCopier<BitVector32>
    {
        public BitVector32 DeepCopy(BitVector32 input, CopyContext _) => new(input);
    }
}