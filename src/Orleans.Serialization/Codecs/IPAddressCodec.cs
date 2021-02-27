using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Net;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class IPAddressCodec : IFieldCodec<IPAddress>, IDerivedTypeCodec
    {
        public static readonly Type CodecFieldType = typeof(IPAddress);

        IPAddress IFieldCodec<IPAddress>.ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        public static IPAddress ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return (IPAddress)ReferenceCodec.ReadReference(ref reader, field, CodecFieldType);
            }

            var length = reader.ReadVarUInt32();
            IPAddress result;
#if NET5_0
            if (reader.TryReadBytes((int)length, out var bytes))
            {
                result = new IPAddress(bytes);
            }
            else
            {
#endif
                var addressBytes = reader.ReadBytes(length);
                result = new IPAddress(addressBytes);
#if NET5_0
            }
#endif

            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        void IFieldCodec<IPAddress>.WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPAddress value)
        {
            WriteField(ref writer, fieldIdDelta, expectedType, value);
        }

        public static void WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPAddress value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
#if NET5_0
            Span<byte> buffer = stackalloc byte[64];
            if (value.TryWriteBytes(buffer, out var length))
            {
                var writable = writer.WritableSpan;
                if (writable.Length > length)
                {
                    writer.WriteVarUInt32((uint)length);
                    buffer.Slice(0, length).CopyTo(writable.Slice(1));
                    writer.AdvanceSpan(length);
                    return;
                }
            }
#endif
            var bytes = value.GetAddressBytes();
            writer.WriteVarUInt32((uint)bytes.Length);
            writer.Write(bytes);
        }
    }

    [RegisterCopier]
    public sealed class IPAddressCopier : IDeepCopier<IPAddress>
    {
        public IPAddress DeepCopy(IPAddress input, CopyContext _) => input;
    }
}
