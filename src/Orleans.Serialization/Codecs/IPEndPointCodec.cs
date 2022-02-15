using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Net;

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
            IPAddressCodec.WriteField(ref writer, 1, IPAddressCodec.CodecFieldType, value.Address);
            UInt16Codec.WriteField(ref writer, 1, UInt16Codec.CodecFieldType, (ushort)value.Port);
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
