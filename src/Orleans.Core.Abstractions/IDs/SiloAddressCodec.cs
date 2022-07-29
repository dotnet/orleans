using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

#nullable enable
namespace Orleans.Runtime.Serialization
{
    /// <summary>
    /// Serializer and deserializer for <see cref="SiloAddress"/> instances.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class SiloAddressCodec : IFieldCodec<SiloAddress>, IDeepCopier<SiloAddress>
    {
        private static readonly Type _codecFieldType = typeof(SiloAddress);

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, SiloAddress? value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta, expectedType);
                return;
            }

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
            IPAddressCodec.WriteField(ref writer, 0, IPAddressCodec.CodecFieldType, value.Endpoint.Address);
            UInt16Codec.WriteField(ref writer, 1, UInt16Codec.CodecFieldType, (ushort)value.Endpoint.Port);
            Int32Codec.WriteField(ref writer, 2, Int32Codec.CodecFieldType, value.Generation);
            writer.WriteEndObject();
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SiloAddress ReadValue<TReaderInput>(ref Reader<TReaderInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<SiloAddress, TReaderInput>(ref reader, field);
            }

            ReferenceCodec.MarkValueField(reader.Session);
            int id = 0;
            Field header = default;

            id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
            if (id != 0) goto invalidData;
            var address = IPAddressCodec.ReadValue(ref reader, header);

            id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
            if (id != 1) goto invalidData;
            var port = UInt16Codec.ReadValue(ref reader, header);

            id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
            if (id != 2) goto invalidData;
            var generation = Int32Codec.ReadValue(ref reader, header);

            while (true)
            {
                id = OrleansGeneratedCodeHelper.ReadHeaderExpectingEndBaseOrEndObject(ref reader, ref header, id);
                if (id == -1)
                {
                    break;
                }

                reader.ConsumeUnknownField(header);
            }

            return SiloAddress.New(address, port, generation);

invalidData: throw new RequiredFieldMissingException("Serialized SiloAddress is missing a required field.");
        }

        /// <inheritdoc />
        public SiloAddress DeepCopy(SiloAddress input, CopyContext context) => input;
    }
}
