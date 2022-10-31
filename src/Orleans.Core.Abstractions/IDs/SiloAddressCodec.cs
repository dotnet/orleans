using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
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
    public sealed class SiloAddressCodec : IFieldCodec<SiloAddress>
    {
        private static readonly Type _codecFieldType = typeof(SiloAddress);

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, SiloAddress? value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
            IPAddressCodec.WriteField(ref writer, 0, IPAddressCodec.CodecFieldType, value.Endpoint.Address);
            uint delta = 2;
            if (value.Endpoint.Port != 0) UInt16Codec.WriteField(ref writer, delta = 1, UInt16Codec.CodecFieldType, (ushort)value.Endpoint.Port);
            if (value.Generation != 0) Int32Codec.WriteField(ref writer, delta, Int32Codec.CodecFieldType, value.Generation);
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

            field.EnsureWireTypeTagDelimited();
            ReferenceCodec.MarkValueField(reader.Session);
            Field header = default;
            int port = 0, generation = 0;

            reader.ReadFieldHeader(ref header);
            if (!header.HasFieldId || header.FieldIdDelta != 0) throw new RequiredFieldMissingException("Serialized SiloAddress is missing its address field.");
            var address = IPAddressCodec.ReadValue(ref reader, header);

            reader.ReadFieldHeader(ref header);
            if (!header.IsEndBaseOrEndObject)
            {
                var id = header.FieldIdDelta;
                if (id == 1)
                {
                    port = UInt16Codec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                    if (header.HasFieldId) id += header.FieldIdDelta;
                }

                if (id == 2)
                {
                    generation = Int32Codec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
            }

            return SiloAddress.New(address, port, generation);
        }
    }
}
