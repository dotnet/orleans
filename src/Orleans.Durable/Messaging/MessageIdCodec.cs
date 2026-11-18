using System;
using System.Buffers;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Messaging;

[RegisterSerializer]
internal sealed class MessageIdCodec(IFieldCodec<string> strings) : IFieldCodec<MessageId>
{
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
        Type? expectedType, MessageId value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(MessageId), WireType.TagDelimited);
        strings.WriteField(ref writer, 0, typeof(string), value.IsDefault ? null! : value.ToString());
        writer.WriteEndObject();
    }

    public MessageId ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);
        MessageId result = default;
        uint fieldId = 0;
        while (true)
        {
            var header = reader.ReadFieldHeader();
            if (header.IsEndBaseOrEndObject) return result;
            fieldId += header.FieldIdDelta;
            if (fieldId == 0)
            {
                var canonical = strings.ReadValue(ref reader, header);
                result = canonical is null ? default : MessageId.Parse(canonical);
            }
            else reader.ConsumeUnknownField(header);
        }
    }
}

[RegisterCopier]
internal sealed class MessageIdCopier : IDeepCopier<MessageId>
{
    public MessageId DeepCopy(MessageId input, CopyContext context) => input;
}
