using System;
using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
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
        private static readonly Type _iPEndPointType = typeof(IPEndPoint);
        private static readonly Type _int32Type = typeof(int);
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
            IPEndPointCodec.WriteField(ref writer, 0U, _iPEndPointType, value.Endpoint);
            Int32Codec.WriteField(ref writer, 1U, _int32Type, value.Generation);
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
            IPEndPoint? endpoint = default;
            int generation = default;
            while (true)
            {
                id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
                if (id == 0)
                {
                    endpoint = IPEndPointCodec.ReadValue(ref reader, header);
                    id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
                }

                if (id == 1)
                {
                    generation = Int32Codec.ReadValue(ref reader, header);
                    id = OrleansGeneratedCodeHelper.ReadHeaderExpectingEndBaseOrEndObject(ref reader, ref header, id);
                }

                if (id == -1)
                {
                    break;
                }

                reader.ConsumeUnknownField(header);
            }

            return SiloAddress.New(endpoint!, generation);
        }

        /// <inheritdoc />
        public SiloAddress DeepCopy(SiloAddress input, CopyContext context) => input;
    }
}
