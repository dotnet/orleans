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
    public sealed class IPEndPointCodec : IFieldCodec<IPEndPoint>
    {
        /// <summary>
        /// The codec field type
        /// </summary>
        public static readonly Type CodecFieldType = typeof(IPEndPoint);

        /// <inheritdoc/>
        IPEndPoint IFieldCodec<IPEndPoint>.ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <inheritdoc/>
        void IFieldCodec<IPEndPoint>.WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPEndPoint value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static IPEndPoint ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return (IPEndPoint)ReferenceCodec.ReadReference(ref reader, field, CodecFieldType);
            }

            var referencePlaceholder = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Field header = default;
            var port = 0;

            var id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, 0);
            if (id != 0) throw new RequiredFieldMissingException("Serialized IPEndPoint is missing its address field.");
            var address = IPAddressCodec.ReadValue(ref reader, header);

            id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
            if (id == 1)
            {
                port = UInt16Codec.ReadValue(ref reader, header);
                id = OrleansGeneratedCodeHelper.ReadHeaderExpectingEndBaseOrEndObject(ref reader, ref header, id);
            }

            while (id >= 0)
            {
                reader.ConsumeUnknownField(header);
                id = OrleansGeneratedCodeHelper.ReadHeaderExpectingEndBaseOrEndObject(ref reader, ref header, id);
            }

            var result = new IPEndPoint(address, port);
            ReferenceCodec.RecordObject(reader.Session, result, referencePlaceholder);
            return result;
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPEndPoint value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.TagDelimited);
            IPAddressCodec.WriteField(ref writer, 0, IPAddressCodec.CodecFieldType, value.Address);
            if (value.Port != 0) UInt16Codec.WriteField(ref writer, 1, UInt16Codec.CodecFieldType, (ushort)value.Port);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Copier for <see cref="IPEndPoint"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class IPEndPointCopier : IDeepCopier<IPEndPoint>
    {
        /// <inheritdoc/>
        public IPEndPoint DeepCopy(IPEndPoint input, CopyContext _) => input;
    }

    /// <summary>
    /// Copier for <see cref="EndPoint"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class EndPointCopier : IDeepCopier<EndPoint>, IDerivedTypeCopier
    {
        /// <inheritdoc/>
        public EndPoint DeepCopy(EndPoint input, CopyContext _) => input;
    }
}
