using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Net;

#nullable enable
namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="IPEndPoint"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class IPEndPointCodec : IFieldCodec<IPEndPoint>, IDerivedTypeCodec
    {
        IPEndPoint IFieldCodec<IPEndPoint>.ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static IPEndPoint ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field)
        {
            if (field.IsReference)
            {
                return ReferenceCodec.ReadReference<IPEndPoint, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var referencePlaceholder = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Field header = default;
            var port = 0;

            reader.ReadFieldHeader(ref header);
            if (!header.HasFieldId || header.FieldIdDelta != 0) throw new RequiredFieldMissingException("Serialized IPEndPoint is missing its address field.");
            var address = IPAddressCodec.ReadValue(ref reader, header);

            reader.ReadFieldHeader(ref header);
            if (header.HasFieldId && header.FieldIdDelta == 1)
            {
                port = UInt16Codec.ReadValue(ref reader, header);
                reader.ReadFieldHeader(ref header);
            }

            reader.ConsumeEndBaseOrEndObject(ref header);

            var result = new IPEndPoint(address, port);
            ReferenceCodec.RecordObject(reader.Session, result, referencePlaceholder);
            return result;
        }

        void IFieldCodec<IPEndPoint>.WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPEndPoint value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, typeof(IPEndPoint), value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(IPEndPoint), WireType.TagDelimited);
            IPAddressCodec.WriteField(ref writer, 0, value.Address);
            if (value.Port != 0) UInt16Codec.WriteField(ref writer, 1, (ushort)value.Port);
            writer.WriteEndObject();
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, IPEndPoint value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceFieldExpected(ref writer, fieldIdDelta, value))
            {
                return;
            }

            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.TagDelimited);
            IPAddressCodec.WriteField(ref writer, 0, value.Address);
            if (value.Port != 0) UInt16Codec.WriteField(ref writer, 1, (ushort)value.Port);
            writer.WriteEndObject();
        }
    }

    [RegisterCopier]
    internal sealed class EndPointCopier : ShallowCopier<EndPoint>, IDerivedTypeCopier { }
}
