using System;
using System.Buffers;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.DurableMessaging;

[RegisterSerializer]
internal sealed class HierarchicalKeyCodec(IFieldCodec<string> strings) : IFieldCodec<HierarchicalKey>
{
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
        Type? expectedType, HierarchicalKey value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(HierarchicalKey), WireType.TagDelimited);
        strings.WriteField(ref writer, 0, typeof(string), value.IsDefault ? null! : value.ToString());
        writer.WriteEndObject();
    }

    public HierarchicalKey ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);
        HierarchicalKey result = default;
        uint fieldId = 0;
        while (true)
        {
            var header = reader.ReadFieldHeader();
            if (header.IsEndBaseOrEndObject) return result;
            fieldId += header.FieldIdDelta;
            if (fieldId == 0)
            {
                var canonical = strings.ReadValue(ref reader, header);
                result = canonical is null ? default : HierarchicalKey.Parse(canonical);
            }
            else reader.ConsumeUnknownField(header);
        }
    }
}

[RegisterCopier]
internal sealed class HierarchicalKeyCopier : IDeepCopier<HierarchicalKey>
{
    public HierarchicalKey DeepCopy(HierarchicalKey input, CopyContext context) => input;
}
