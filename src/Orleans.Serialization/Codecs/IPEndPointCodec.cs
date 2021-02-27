using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Net;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class IPEndPointCodec : IFieldCodec<IPEndPoint>
    {
        public static readonly Type CodecFieldType = typeof(IPEndPoint);

        IPEndPoint IFieldCodec<IPEndPoint>.ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        void IFieldCodec<IPEndPoint>.WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPEndPoint value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static IPEndPoint ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return (IPEndPoint)ReferenceCodec.ReadReference(ref reader, field, CodecFieldType);
            }

            var referencePlaceholder = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            IPAddress address = default;
            ushort port = 0;
            int id = 0;
            Field header = default;
            while (true)
            {
                id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
                if (id == 1)
                {
                    address = IPAddressCodec.ReadValue(ref reader, header);
                    id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
                }

                if (id == 2)
                {
                    port = UInt16Codec.ReadValue(ref reader, header);
                    id = OrleansGeneratedCodeHelper.ReadHeaderExpectingEndBaseOrEndObject(ref reader, ref header, id);
                }

                if (id != -1)
                {
                    reader.ConsumeUnknownField(header);
                }
                else
                {
                    break;
                }
            }

            var result = new IPEndPoint(address, port);
            ReferenceCodec.RecordObject(reader.Session, result, referencePlaceholder);
            return result;
        }

        public static void WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPEndPoint value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.TagDelimited);
            IPAddressCodec.WriteField(ref writer, 1, IPAddressCodec.CodecFieldType, value.Address);
            UInt16Codec.WriteField(ref writer, 1, UInt16Codec.CodecFieldType, (ushort)value.Port);
            writer.WriteEndObject();
        }
    }

    [RegisterCopier]
    public sealed class IPEndPointCopier : IDeepCopier<IPEndPoint>
    {
        public IPEndPoint DeepCopy(IPEndPoint input, CopyContext _) => input;
    }

    [RegisterCopier]
    public sealed class EndPointCopier : IDeepCopier<EndPoint>, IDerivedTypeCopier
    {
        public EndPoint DeepCopy(EndPoint input, CopyContext _) => input;
    }
}
