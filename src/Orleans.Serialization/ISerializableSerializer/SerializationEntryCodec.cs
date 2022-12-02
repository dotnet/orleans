using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Security;

namespace Orleans.Serialization
{
    internal sealed class SerializationEntryCodec : IFieldCodec<SerializationEntrySurrogate>
    {
        [SecurityCritical]
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            SerializationEntrySurrogate value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(SerializationEntrySurrogate), WireType.TagDelimited);
            StringCodec.WriteField(ref writer, 0, value.Name);
            ObjectCodec.WriteField(ref writer, 1, value.Value);
            if (value.ObjectType is { } objectType)
            {
                TypeSerializerCodec.WriteField(ref writer, 1, objectType);
            }

            writer.WriteEndObject();
        }

        [SecurityCritical]
        public SerializationEntrySurrogate ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            field.EnsureWireTypeTagDelimited();
            ReferenceCodec.MarkValueField(reader.Session);
            var result = new SerializationEntrySurrogate();
            uint fieldId = 0;
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
                        result.Name = StringCodec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        result.Value = ObjectCodec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        result.ObjectType = TypeSerializerCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            return result;
        }
    }
}