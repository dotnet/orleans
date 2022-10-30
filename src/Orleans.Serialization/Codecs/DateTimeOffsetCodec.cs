using System;
using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="DateTimeOffset"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class DateTimeOffsetCodec : IFieldCodec<DateTimeOffset>
    {
        /// <inheritdoc/>
        void IFieldCodec<DateTimeOffset>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, DateTimeOffset value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <inheritdoc/>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, DateTimeOffset value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(DateTimeOffset), WireType.TagDelimited);
            DateTimeCodec.WriteField(ref writer, 0, typeof(DateTime), value.DateTime);
            TimeSpanCodec.WriteField(ref writer, 1, typeof(TimeSpan), value.Offset);
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        DateTimeOffset IFieldCodec<DateTimeOffset>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <inheritdoc/>
        public static DateTimeOffset ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            field.EnsureWireTypeTagDelimited();

            uint fieldId = 0;
            TimeSpan offset = default;
            DateTime dateTime = default;
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
                        dateTime = DateTimeCodec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        offset = TimeSpanCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            return new DateTimeOffset(dateTime, offset);
        }
    }
}