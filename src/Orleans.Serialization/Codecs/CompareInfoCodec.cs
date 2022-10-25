using System;
using System.Buffers;
using System.Globalization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="CompareInfo"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class CompareInfoCodec : IFieldCodec<CompareInfo>
    {
        /// <inheritdoc/>
        public CompareInfo ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<CompareInfo, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            uint fieldId = 0;
            string name = null;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 0:
                        name = StringCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = CompareInfo.GetCompareInfo(name);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, CompareInfo value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);
            StringCodec.WriteField(ref writer, 0, StringCodec.CodecFieldType, value.Name);
            writer.WriteEndObject();
        }

        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for {nameof(CompareInfo)} fields. {field}");
    }

    /// <summary>
    /// Copier for <see cref="CompareInfo"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class CompareInfoCopier : IDeepCopier<CompareInfo>, IOptionalDeepCopier
    {
        /// <inheritdoc/>
        public CompareInfo DeepCopy(CompareInfo input, CopyContext context) => input;
    }
}